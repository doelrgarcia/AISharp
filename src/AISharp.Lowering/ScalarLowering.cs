using AISharp.Compiler;
using System.Text;

namespace AISharp.Lowering;

public static partial class AISharpLowering
{
    private static string ScalarTypeName(ScalarType type) => type switch
    {
        ScalarType.Int => "int", ScalarType.Bool => "bool", ScalarType.Unit => "void",
        ScalarType.Byte => "byte", ScalarType.SByte => "sbyte", ScalarType.Short => "short", ScalarType.UShort => "ushort",
        ScalarType.UInt => "uint", ScalarType.Long => "long", ScalarType.ULong => "ulong",
        ScalarType.Char => "char", ScalarType.F32 => "float", ScalarType.F64 => "double", ScalarType.Decimal => "decimal",
        _ => throw new InvalidOperationException("Unsupported scalar type.")
    };
    private static string ScalarCode(ScalarExpression expression)
    {
        var left = expression.Left is null ? "" : ScalarCode(expression.Left);
        // Increment MIR carries a reference-read placeholder for native storage updates.
        // C#'s typed increment evaluates the local once and preserves pre/post values.
        var right = expression.Right is null || expression.Operation is "pre++" or "pre--" or "post++" or "post--"
            ? "" : ScalarCode(expression.Right);
        return expression.Operation switch
        {
            "literal" => ScalarConstants.IsInteger(expression.Type)
                ? ScalarIntegerLiteral(expression) : expression.Type switch
                {
                    ScalarType.F32 or ScalarType.F64 => ScalarFloatingLiteral(expression),
                    ScalarType.Decimal => $"decimal.CreateChecked<decimal>({expression.Value}M)",
                    _ => expression.Value!
                },
            "local" => expression.Value!,
            "cast" or "convert" => $"{ScalarOverflow(expression)}(({ScalarTypeName(expression.Type)})({left}))",
            "call" => $"@{expression.Value}({string.Join(", ", (expression.Arguments ?? []).Select(ScalarCode))})",
            "unary+" => $"{ScalarOverflow(expression)}(+{left})", "unary-" => $"{ScalarOverflow(expression)}(-{left})", "unary!" => $"(!{left})",
            "unary~" => $"(~{left})",
            "pre++" => $"{ScalarOverflow(expression)}(++{left})", "pre--" => $"{ScalarOverflow(expression)}(--{left})",
            "post++" => $"{ScalarOverflow(expression)}({left}++)", "post--" => $"{ScalarOverflow(expression)}({left}--)",
            "+" or "-" or "*" or "/" or "%" or "<" or "<=" or ">" or ">=" or "==" or "!="
                or "&&" or "||" or "&" or "|" or "^" or "=" or "+=" or "-=" or "&=" or "|=" or "^="
                or "*=" or "/=" or "%=" or "<<" or ">>" or ">>>" => $"{ScalarOverflow(expression)}({left} {expression.Operation} {right})",
            _ => throw new InvalidOperationException($"Unrecognized typed scalar MIR '{expression.Operation}'.")
        };
    }
    private static string ScalarOverflow(ScalarExpression expression) => expression.Wraps ? "unchecked" : "checked";
    private static string ScalarFloatingLiteral(ScalarExpression expression)
    {
        var type = ScalarTypeName(expression.Type);
        var special = expression.Value switch { "NaN" => "NaN", "Infinity" => "PositiveInfinity", "-Infinity" => "NegativeInfinity", _ => null };
        return special is not null ? $"{type}.{special}"
            : $"{type}.CreateChecked<{type}>({expression.Value}{(expression.Type == ScalarType.F32 ? "F" : "D")})";
    }
    private static string ScalarIntegerLiteral(ScalarExpression expression)
    {
        if (expression.Type == ScalarType.Int) return $"global::System.Int32.CreateChecked<int>({expression.Value})";
        if (expression.Type == ScalarType.Char) return $"checked((char)int.CreateChecked<int>({expression.Value}))";
        var type = ScalarTypeName(expression.Type);
        var storage = expression.Type is ScalarType.Long ? "long" : expression.Type is ScalarType.ULong ? "ulong" : expression.Type is ScalarType.UInt ? "uint" : "int";
        var suffix = storage switch { "long" => "L", "ulong" => "UL", "uint" => "U", _ => "" };
        return $"{type}.CreateChecked<{storage}>({expression.Value}{suffix})";
    }
    private static void LowerScalarFunction(StringBuilder sb, ScalarFunction function)
    {
        var parameters = function.Parameters.Select(p => $"{ScalarTypeName(p.Type)} {p.Name}");
        sb.AppendLine($"        public static {ScalarTypeName(function.ReturnType)} @{function.Name}({string.Join(", ",parameters)})");
        sb.AppendLine("        {");
        sb.AppendLine("            checked"); sb.AppendLine("            {");
        foreach (var contract in function.Contracts.Where(c => c.Kind == ContractKind.Requires)) EmitScalarContract(sb, contract);
        EmitScalarStatements(sb, function.Body, function);
        if (function.ReturnType == ScalarType.Unit)
            foreach (var contract in function.Contracts.Where(c => c.Kind != ContractKind.Requires)) EmitScalarContract(sb, contract);
        sb.AppendLine("            }"); sb.AppendLine("        }");
    }
    private static void EmitScalarContract(StringBuilder sb, ScalarContract contract) =>
        EmitContractCheckCall(sb, new(contract.Kind, contract.Text, [], MirContractCheckMode.Runtime, contract.Location), ScalarCode(contract.Expression));
    private static void EmitScalarStatements(StringBuilder sb, IReadOnlyList<ScalarStatement> statements, ScalarFunction function)
    {
        foreach (var statement in statements)
        {
            sb.AppendLine($"            // AI# {statement.Location.Line}:{statement.Location.Column}");
            switch (statement.Kind)
            {
                case "local": sb.AppendLine(ScalarLocal(statement)+";"); break;
                case "expression": sb.AppendLine($"            {(statement.Expression!.Type == ScalarType.Unit ? "" : "_ = ")}{ScalarCode(statement.Expression)};"); break;
                case "return":
                    sb.AppendLine("            {");
                    if (statement.Expression is not null)
                        sb.AppendLine($"            {ScalarTypeName(function.ReturnType)} __scalar_result = {ScalarCode(statement.Expression)};");
                    foreach (var contract in function.Contracts.Where(c => c.Kind != ContractKind.Requires)) EmitScalarContract(sb, contract);
                    sb.AppendLine(statement.Expression is null ? "            return;" : "            return __scalar_result;");
                    sb.AppendLine("            }"); break;
                case "if":
                    sb.AppendLine($"            if ({ScalarCode(statement.Expression!)})");
                    EmitScalarBlock(sb, statement.Body!, function);
                    if (statement.Alternative!.Count != 0)
                    { sb.AppendLine("            else"); EmitScalarBlock(sb, statement.Alternative, function); }
                    break;
                case "while": sb.AppendLine($"            while ({ScalarCode(statement.Expression!)})"); EmitScalarBlock(sb, statement.Body!, function); break;
                case "dowhile": sb.AppendLine("            do"); EmitScalarBlock(sb, statement.Body!, function); sb.AppendLine($"            while ({ScalarCode(statement.Expression!)});"); break;
                case "for":
                    sb.AppendLine($"            for ({ScalarLocal(statement.Initializer!)}; {ScalarCode(statement.Expression!)}; _ = {ScalarCode(statement.Increment!)})");
                    EmitScalarBlock(sb, statement.Body!, function); break;
                case "break": case "continue": sb.AppendLine($"            {statement.Kind};"); break;
                case "block": EmitScalarBlock(sb, statement.Body!, function); break;
                default: throw new InvalidOperationException($"Unrecognized typed scalar statement '{statement.Kind}'.");
            }
        }
    }
    private static string ScalarLocal(ScalarStatement statement) =>
        $"            {ScalarTypeName(statement.Type)} {statement.Name}" +
        (statement.Expression is null ? "" : $" = {ScalarCode(statement.Expression)}");
    private static void EmitScalarBlock(StringBuilder sb, IReadOnlyList<ScalarStatement> statements, ScalarFunction function)
    {
        sb.AppendLine("            {"); EmitScalarStatements(sb, statements, function); sb.AppendLine("            }");
    }
}
