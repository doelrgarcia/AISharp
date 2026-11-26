using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private const string StringArrayName = "__aisharp_string_array";
    private const string StringArrayIteratorName = "__aisharp_array_iterator";
    private static string ExtractStringArrays(string source, out Diagnostic? error)
    {
        error = null;
        var tokens = new Lexer(source).Lex();
        var reserved = tokens.FirstOrDefault(t => t.Kind == TokenKind.Identifier && IdentifierName(t.Text).StartsWith("__aisharp_", StringComparison.Ordinal));
        if (reserved is not null) error = new("AI3106", DiagnosticSeverity.Error, $"Identifier '{reserved.Text}' is reserved for native array storage.", reserved.Location);
        var used = Enumerable.Range(0, Math.Max(0, tokens.Count - 2)).Any(i => tokens[i].Text == "string" && tokens[i + 1].Text == "[" && (tokens[i + 2].Text == "]" || i > 0 && tokens[i - 1].Text == "new"));
        if (!used) return source;
        return source + "\n" + """
sealed class __aisharp_string_array : __IEnumerable_string {
    public readonly Length: int
    public override fn GetEnumerator() -> __IEnumerator_string
        ensures true
    { return new __aisharp_array_iterator(this) }
}
sealed class __aisharp_array_iterator : __IEnumerator_string {
    private source: string[]
    private position: int
    public fn new(source: string[]) -> __aisharp_array_iterator
        ensures true
    { this.source = source
      this.position = -1 }
    public override fn MoveNext() -> bool
        ensures true
    { if (position < source.Length) { position++ }
      return position < source.Length }
    public override fn get_Current() -> string
        ensures true
    { if (position < 0 || position >= source.Length) { throw new InvalidOperationException() }
      return source[position] }
    public override fn Dispose() -> void
        ensures true
    { }
}
""";
    }

    private sealed partial class ExtendedBinder
    {
        private bool IsStringArray(ScalarType type) => Has(ScalarFeatures.ProcessArguments) && IsStruct(type) && StructOf(type).IsStringArray;
        private ScalarType StringArrayType(SourceLocation at)
        {
            if (!Has(ScalarFeatures.ProcessArguments) || !Has(ScalarFeatures.Classes) || !Has(ScalarFeatures.Strings) || StructNamed(StringArrayName) is not { } array)
            { Fail("AI3106", "String arrays are unavailable in this profile.", at); return default; }
            return array.Id;
        }
        private bool StringArrayTypeAhead(int offset = 0) => Has(ScalarFeatures.ProcessArguments) && TokenAt(_position + offset).Text == "string" && TokenAt(_position + offset + 1).Text == "[" && TokenAt(_position + offset + 2).Text == "]";
        private bool StringArrayCastAt() => Has(ScalarFeatures.ProcessArguments) && Current.Text == "(" && TokenAt(_position + 1).Text == "string"
            && TokenAt(_position + 2).Text == "[" && TokenAt(_position + 3).Text == "]" && TokenAt(_position + 4).Text == ")";
        private ScalarExpression StringArrayCast()
        {
            Next(); var type = TypeToken(); Expect(")"); var operand = Unary(); Read(operand);
            return ReferenceCast(Type(type.Text, type.Location), operand, type.Location);
        }
        private ScalarExpression NewStringArray(Lexeme keyword)
        {
            var element = Next(); Expect("["); var values = new List<ScalarExpression>(); ScalarExpression? count = null;
            if (!Take("]")) { count = Expression(); Read(count); count = Coerce(count, ScalarType.Int); Expect("]"); }
            if (Take("{"))
            {
                NewLines();
                while (Current.Text != "}")
                {
                    var value = Expression(); Read(value); values.Add(Coerce(value, ScalarType.String)); NewLines();
                    if (!Take(",")) break; NewLines();
                }
                Expect("}");
                if (count is not null && (ConstantOf(count) is not BigInteger n || n != values.Count))
                    Fail("AI3106", "An array initializer requires a matching constant length.", count.Location);
                count ??= new("literal", ScalarType.Int, keyword.Location, values.Count.ToString(CultureInfo.InvariantCulture));
            }
            if (count is null) Fail("AI3106", "An array creation needs a length or an initializer.", element.Location);
            if (ConstantOf(count!) is BigInteger negative && negative.Sign < 0) Fail("AI3112", "An array length cannot be a negative constant.", count!.Location);
            return new("array-new", StringArrayType(element.Location), keyword.Location, Left: count) { Arguments = values };
        }
        private ScalarExpression ArrayAssignment(ScalarExpression target, string op, ScalarExpression right, SourceLocation at)
        {
            Write(target);
            if (IsTypedArray(target.Left!.Type))
            {
                if (op == "=") return new("array-store", target.Type, at, "=", Left: target.Left, Right: target.Right) { Arguments = [Coerce(right, target.Type)] };
                return ReferenceUpdate(target, op[..^1], right, false, at);
            }
            if (op is not ("=" or "+=")) Fail("AI3107", $"Operator '{op}' is not defined for string array elements.", at);
            return new("array-store", ScalarType.String, at, op, Left: target.Left, Right: target.Right) { Arguments = [Coerce(right, ScalarType.String)] };
        }
    }
}
