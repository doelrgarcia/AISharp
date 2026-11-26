using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AISharp.Compiler;

namespace AISharp.Lowering;

/// <summary>Closed native local-storage capability; unsupported operators never reach C emission.</summary>
public static class NativeScalarCompiler
{
    public const string Capability = "native-locals-0.1";
    public sealed record Preparation(string SourceSha256, string? CSource, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public bool Success => CSource is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    // Slot numbers are compiler-assigned. Source names/text never become C identifiers or expressions.
    private abstract record Value(SourceLocation Location);
    private sealed record Constant(int Number, SourceLocation At) : Value(At);
    private sealed record Read(int Slot, SourceLocation At) : Value(At);
    private abstract record Statement(SourceLocation Location);
    private sealed record Declare(int Slot, Value? Initializer, SourceLocation At) : Statement(At);
    private sealed record Store(int Slot, Value Value, SourceLocation At) : Statement(At);
    private sealed record Return(Value Value, SourceLocation At) : Statement(At);
    private sealed record Block(IReadOnlyList<Statement> Statements, SourceLocation At) : Statement(At);
    private sealed record Binding(int Slot, bool Mutable, bool Assigned);
    private sealed class Rejected(SourceLocation at, string message) : Exception(message)
    { public SourceLocation At { get; } = at; }

    public static Preparation Prepare(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var parsed = AISharpParser.Parse(source, ScalarLanguage.StrictProfile);
        var diagnostics = parsed.Diagnostics.ToList();
        if (!parsed.Success) return new(hash, null, diagnostics);
        try
        {
            var functions = parsed.Document.ScalarFunctions;
            if (functions.Count != 1) throw new Rejected(new(1, 1, 0), "Native locals require one entry function.");
            var function = functions[0];
            if (function.Name != "main" || function.Parameters.Count != 0 || function.ReturnType != ScalarType.Int)
                throw new Rejected(function.Location, "Native locals require fn main() -> int.");
            if (function.Contracts.Count != 1 || function.Contracts[0].Kind != ContractKind.Ensures)
                throw new Rejected(function.Location, "Native locals require exactly one ensures true contract.");
            if (function.Contracts[0].Expression is not { Operation: "literal", Type: ScalarType.Bool, Value: "true", Left: null, Right: null })
                throw new Rejected(function.Contracts[0].Expression.Location, "Only the proven ensures true contract is supported.");
            var verifier = new Verifier();
            var (body, returns) = verifier.Verify(function.Body);
            if (!returns) throw new Rejected(function.Location, "Native entry must return on its reaching path.");
            return new(hash, Emit(body), diagnostics);
        }
        catch (Rejected error)
        {
            diagnostics.Add(new("AI3201", DiagnosticSeverity.Error, error.Message, error.At));
            return new(hash, null, diagnostics);
        }
    }

    private sealed class Verifier
    {
        private readonly Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
        private int nextSlot;
        private Binding Lookup(ScalarExpression expression, bool reading)
        {
            if (expression.Operation != "local" || expression.Type != ScalarType.Int || expression.Value is null
                || !bindings.TryGetValue(expression.Value, out var binding))
                throw new Rejected(expression.Location, "Expected an in-scope Int32 local.");
            if (reading && !binding.Assigned) throw new Rejected(expression.Location, "Local read requires definite assignment.");
            return binding;
        }
        private Value VerifyValue(ScalarExpression? expression, SourceLocation fallback)
        {
            if (expression is null) throw new Rejected(fallback, "Expected an Int32 value.");
            if (expression.Type != ScalarType.Int) throw new Rejected(expression.Location, "Native local storage supports Int32 values only.");
            if (expression.Operation == "local") return new Read(Lookup(expression, true).Slot, expression.Location);
            if (expression is { Operation: "literal", Left: null, Right: null }
                && int.TryParse(expression.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                return new Constant(number, expression.Location);
            if (expression is { Operation: "unary-", Right: null, Left: { Operation: "literal", Type: ScalarType.Int, Left: null, Right: null } operand }
                && int.TryParse(operand.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
                return new Constant(-magnitude, expression.Location);
            throw new Rejected(expression.Location, "Native local values must be signed decimal literals or prior initialized locals.");
        }
        public (IReadOnlyList<Statement> Body, bool Returns) Verify(IReadOnlyList<ScalarStatement> statements)
        {
            var result = new List<Statement>(); var locals = new List<string>(); var returns = false;
            foreach (var statement in statements)
            {
                if (returns) throw new Rejected(statement.Location, "Unreachable statements are outside native locals.");
                switch (statement.Kind)
                {
                    case "local":
                    {
                        if (statement.Type != ScalarType.Int || statement.Name is null || bindings.ContainsKey(statement.Name))
                            throw new Rejected(statement.Location, "Expected a uniquely bound Int32 declaration.");
                        var value = statement.Expression is null ? null : VerifyValue(statement.Expression, statement.Location);
                        if (!statement.Mutable && value is null) throw new Rejected(statement.Location, "Immutable let requires initialization.");
                        var slot = nextSlot++;
                        bindings.Add(statement.Name, new(slot, statement.Mutable, value is not null)); locals.Add(statement.Name);
                        result.Add(new Declare(slot, value, statement.Location)); break;
                    }
                    case "expression":
                    {
                        var expression = statement.Expression;
                        if (expression is not { Operation: "=", Type: ScalarType.Int, Left: not null, Right: not null })
                            throw new Rejected(expression?.Location ?? statement.Location, "Native local statements support simple assignment only.");
                        var binding = Lookup(expression.Left, false);
                        if (!binding.Mutable) throw new Rejected(expression.Left.Location, "Cannot assign immutable let.");
                        var value = VerifyValue(expression.Right, expression.Location);
                        bindings[expression.Left.Value!] = binding with { Assigned = true };
                        result.Add(new Store(binding.Slot, value, statement.Location)); break;
                    }
                    case "block":
                    {
                        var nested = Verify(statement.Body ?? []);
                        result.Add(new Block(nested.Body, statement.Location)); returns = nested.Returns; break;
                    }
                    case "return":
                        result.Add(new Return(VerifyValue(statement.Expression, statement.Location), statement.Location)); returns = true; break;
                    default: throw new Rejected(statement.Location, "Statement is outside native local storage capability.");
                }
            }
            foreach (var name in locals) bindings.Remove(name);
            return (result, returns);
        }
    }

    private static string Emit(IReadOnlyList<Statement> body)
    {
        static string Slot(int slot) => "ai_local_" + slot.ToString(CultureInfo.InvariantCulture);
        static string Expression(Value value) => value switch
        {
            Constant { Number: int.MinValue } => "(-2147483647 - 1)",
            Constant number => number.Number.ToString(CultureInfo.InvariantCulture),
            Read read => Slot(read.Slot),
            _ => throw new InvalidOperationException("Unverified native value.")
        };
        var c = new StringBuilder("#include <stdint.h>\n#include <stdio.h>\n#include <inttypes.h>\n_Static_assert(sizeof(int32_t) == 4, \"AI# requires int32_t\");\nstatic int32_t aisharp_entry(void) {\n");
        void Statements(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
                switch (statement)
                {
                    case Declare local:
                        c.Append("int32_t ").Append(Slot(local.Slot));
                        if (local.Initializer is not null) c.Append(" = ").Append(Expression(local.Initializer));
                        // Address-only use suppresses unused-local warnings without reading uninitialized storage.
                        c.Append("; (void)&").Append(Slot(local.Slot)).AppendLine(";"); break;
                    case Store store: c.Append(Slot(store.Slot)).Append(" = ").Append(Expression(store.Value)).AppendLine(";"); break;
                    case Return returned: c.Append("return ").Append(Expression(returned.Value)).AppendLine(";"); break;
                    case Block block: c.AppendLine("{"); Statements(block.Statements); c.AppendLine("}"); break;
                    default: throw new InvalidOperationException("Unverified native statement.");
                }
        }
        Statements(body);
        c.Append("}\nint main(void) { int32_t result = aisharp_entry();\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" PRId32 \"}\\n\", result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
        return c.ToString();
    }
}
