using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AISharp.Compiler;

namespace AISharp.Lowering;

/// <summary>Experimental native group N0. No host-language expression text is accepted.</summary>
public static class NativeReturnCompiler
{
    public const string Capability = "native-return-0.1";
    public sealed record Preparation(string SourceSha256, string? CSource, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public bool Success => CSource is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    // A closed, backend-neutral representation of the one verified operation in N0.
    private sealed record ReturnInt32(int Value, SourceLocation Location);

    public static Preparation Prepare(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var parsed = AISharpParser.Parse(source, ScalarLanguage.StrictProfile);
        var diagnostics = parsed.Diagnostics.ToList();
        Preparation Reject(string message, SourceLocation location)
        {
            diagnostics.Add(new("AI3200", DiagnosticSeverity.Error, message, location));
            return new(hash, null, diagnostics);
        }
        if (!parsed.Success) return new(hash, null, diagnostics);
        var functions = parsed.Document.ScalarFunctions;
        if (functions.Count != 1) return Reject("Native N0 requires exactly one entry function.", new(1, 1, 0));
        var function = functions[0];
        if (function.Name != "main" || function.Parameters.Count != 0 || function.ReturnType != ScalarType.Int)
            return Reject("Native N0 requires fn main() -> int.", function.Location);
        if (function.Contracts.Count != 1 || function.Contracts[0].Kind != ContractKind.Ensures)
            return Reject("Native N0 requires exactly one ensures true contract.", function.Location);
        if (function.Contracts[0].Expression is not { Operation: "literal", Type: ScalarType.Bool, Value: "true", Left: null, Right: null })
            return Reject("Native N0 can prove only ensures true; all other contracts require a later capability.", function.Contracts[0].Expression.Location);
        if (function.Body.Count != 1 || function.Body[0].Kind != "return")
            return Reject("Native N0 requires one return of a signed decimal integer literal.", function.Body.FirstOrDefault()?.Location ?? function.Location);
        var expression = function.Body[0].Expression;
        int value;
        if (expression is { Operation: "literal", Type: ScalarType.Int, Left: null, Right: null }
            && int.TryParse(expression.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)) { }
        else if (expression is { Operation: "unary-", Type: ScalarType.Int, Right: null,
                     Left: { Operation: "literal", Type: ScalarType.Int, Left: null, Right: null } operand }
                 && int.TryParse(operand.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
            value = -magnitude;
        else return Reject("Native N0 supports only signed decimal integer literals.", expression?.Location ?? function.Location);
        var verified = new ReturnInt32(value, expression!.Location);
        // Constants are formatted from a verified Int32, never copied from source.
        // Spell MIN as a representable subtraction to avoid C literal typing differences.
        var constant = verified.Value == int.MinValue ? "(-2147483647 - 1)" : verified.Value.ToString(CultureInfo.InvariantCulture);
        var c = "#include <stdint.h>\n#include <stdio.h>\n#include <inttypes.h>\n"
            + "_Static_assert(sizeof(int32_t) == 4, \"AI# requires int32_t\");\n"
            + "static int32_t aisharp_entry(void) { return " + constant + "; }\n"
            + "int main(void) { int32_t result = aisharp_entry();\n"
            + " if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" PRId32 \"}\\n\", result) < 0) return 74;\n"
            + " return fflush(stdout) == 0 ? 0 : 74; }\n";
        return new(hash, c, diagnostics);
    }
}
