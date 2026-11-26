using System.Globalization;
using System.Numerics;

namespace AISharp.Compiler;

/// <summary>Native integer binding target. It is explicit and independent of the front-end process architecture.</summary>
public sealed record ScalarNativeIntegerInfo(int PointerBits, bool Used);

public static partial class ScalarLanguage
{
    private sealed class NativeIntegerContext(int bits) { public int Bits { get; } = bits; public bool Used; }
    [ThreadStatic] private static NativeIntegerContext? nativeIntegerContext;

    public static ParseResult ParseExtended(string source, ScalarFeatures features, int pointerBits) => ParseExtended(source, features, pointerBits, out _, out _, out _);
    public static ParseResult ParseExtended(string source, ScalarFeatures features, int pointerBits, out IReadOnlyList<ScalarEnum> enums, out IReadOnlyList<ScalarStruct> structs) =>
        ParseExtended(source, features, pointerBits, out enums, out structs, out _);
    public static ParseResult ParseExtended(string source, ScalarFeatures features, int pointerBits, out IReadOnlyList<ScalarEnum> enums, out IReadOnlyList<ScalarStruct> structs, out ScalarNativeIntegerInfo target)
    {
        if (pointerBits is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(pointerBits));
        if(activeCompilation is null&&(features&ScalarFeatures.CompilationUnits)!=0&&RequiresCompilationBinding(source))
        {
            var compilation=BindCompilation(source);
            if(!compilation.Success)
            {enums=[];structs=[];target=new(pointerBits,false);var empty=AISharpParser.Parse("module compilation.invalid\nintent \"Invalid compilation group\"\n");return empty with{Diagnostics=compilation.Diagnostics.Select(d=>d.Diagnostic).ToArray()};}
            var parsed=ParseCompilation(compilation,features,pointerBits,out enums,out structs,out target);
            return parsed with{Diagnostics=parsed.Diagnostics.Select(d=>compilation.MapDiagnostic(d).Diagnostic).ToArray()};
        }
        var previous = nativeIntegerContext; var context = new NativeIntegerContext(pointerBits); nativeIntegerContext = context;
        try { var result = ParseExtendedCore(source, features, out enums, out structs); target = new(pointerBits, context.Used); return result; }
        finally { nativeIntegerContext = previous; }
    }

    private sealed partial class ExtendedBinder
    {
        private readonly NativeIntegerContext _nativeTarget = nativeIntegerContext ?? new(64);
        private ScalarType NativeStorage(ScalarType type) => ScalarConstants.NativeStorage(type, _nativeTarget.Bits);
        private static bool NativeInvolved(ScalarExpression e) => ScalarConstants.IsNative(e.Type)
            || e.Left is not null && ScalarConstants.IsNative(e.Left.Type) || e.Right is not null && ScalarConstants.IsNative(e.Right.Type);

        // A native alias is also a legal value name. Preserve parentheses around that value
        // before binary/postfix operators and delimiters, while (nint)1 and (nint)~1 remain casts.
        private bool NativeAliasParenthesizedValue()
        {
            var name = TokenAt(_position + 1).Text;
            if (name is not ("nint" or "nuint") || !(IsLocalName(name) || _struct is not null && NameMemberExists(_struct.Id, name))) return false;
            return TokenAt(_position + 3).Text is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^"
                or "<<" or ">>" or ">>>" or "<" or ">" or "<=" or ">=" or "==" or "!=" or "&&" or "||"
                or "?" or "??" or "++" or "--" or "." or "[" or ")" or "]" or "}" or "," or ";" or "\n" or "";
        }

        private bool NativeStaticAt() => Has(ScalarFeatures.NativeIntegers) && (ImportValueType(Current.Text,Current.Location) is ScalarType.NInt or ScalarType.NUInt && TokenAt(_position+1).Text=="." || QualifiedBuiltinAt(_position,expression:true) is {Alias:"nint" or "nuint"} q&&TokenAt(q.End).Text=="." || Current.Text is "nint" or "nuint"
            && TokenAt(_position + 1).Text == "." && _typeArgs?.ContainsKey(Current.Text)!=true && _memberArgs?.ContainsKey(Current.Text)!=true && !IsLocalName(Current.Text) && StructNamed(Current.Text) is null
            && !(_struct is not null && NameMemberExists(_struct.Id, Current.Text)) && !_functions.Any(f => f.Name == Current.Text) && ScopedCompilationFunction(Current.Text,Current.Location) is null);
        private ScalarExpression NativeStatic()
        {
            var name = TypeHeadToken(); var type = Type(name.Text, name.Location); Expect("."); var member = Next(); member=member with {Text=member.Name};
            if (member.Text is not ("MinValue" or "MaxValue" or "Size")) Fail("AI3103", $"'{name.Text}' has no supported member '{member.Text}'.", member.Location);
            return new("native-info", member.Text == "Size" ? ScalarType.Int : type, member.Location, member.Text + ":" + ScalarConstants.Spelling(type));
        }

        // C# permits only architecture-independent native constants. Evaluate at both widths and
        // retain a constant only when both results agree and fit the 32-bit constant range.
        private ScalarExpression FoldNative(ScalarExpression node)
        {
            if (node.Operation is "/" or "%" && node.Right is not null && ConstantOf(node.Right) is BigInteger zero && zero.IsZero)
                Fail("AI3112", "Constant division or remainder by zero.", node.Location);
            object? Evaluate(int bits)
            {
                ScalarType Storage(ScalarType t) => ScalarConstants.NativeStorage(t, bits);
                var type = Storage(node.Type);
                object? Left() => node.Left is null ? null : ConstantOf(node.Left);
                object? Right() => node.Right is null ? null : ConstantOf(node.Right);
                switch (node.Operation)
                {
                    case "literal": return BigInteger.Parse(node.Value!, CultureInfo.InvariantCulture);
                    case "local": return _constLocals.TryGetValue(node.Value!, out var known) ? known : null;
                    case "convert" or "cast" when Left() is { } source:
                        if (IsFloat(node.Type)) return source is BigInteger integer ? ScalarConstants.IntegerToFloat(integer, node.Type) : (double)source;
                        if (IsDecimal(node.Type)) return source is BigInteger integer ? (decimal)integer : (decimal)(double)source;
                        if (source is double floating) return ScalarConstants.FloatConstantToInteger(type, floating, node.Wraps, out var f) == ScalarFault.None ? f : null;
                        if (source is decimal dec) { var d = new BigInteger(decimal.Truncate(dec)); return ScalarConstants.Fits(d, type) ? d : null; }
                        return source is BigInteger number && ScalarConstants.Convert(type, number, node.Operation == "cast" && node.Wraps, out var converted) == ScalarFault.None ? converted : null;
                    case "unary+": return Left();
                    case "unary-" when Left() is BigInteger neg: return ScalarConstants.Negate(type, neg, node.Wraps, out var negated) == ScalarFault.None ? negated : null;
                    case "unary~" when Left() is BigInteger not: return ScalarConstants.BitNot(type, not);
                    case "+" or "-" or "*" or "/" or "%" when Left() is BigInteger a && Right() is BigInteger b:
                        return ScalarConstants.Arithmetic(node.Operation, type, a, b, node.Wraps, out var arithmetic) == ScalarFault.None ? arithmetic : null;
                    case "&" or "|" or "^" when Left() is BigInteger a && Right() is BigInteger b: return ScalarConstants.Bitwise(node.Operation, a, b);
                    case "<<" or ">>" or ">>>" when Left() is BigInteger a && Right() is BigInteger b: return ScalarConstants.Shift(node.Operation, type, a, b);
                    case "==" or "!=" or "<" or "<=" or ">" or ">=" when Left() is BigInteger a && Right() is BigInteger b:
                        var compare = a.CompareTo(b); return node.Operation switch { "==" => compare == 0, "!=" => compare != 0, "<" => compare < 0, "<=" => compare <= 0, ">" => compare > 0, _ => compare >= 0 };
                    default: return null;
                }
            }
            var a = Evaluate(32); var b = Evaluate(64);
            if (a is not null && a.Equals(b) && (!ScalarConstants.IsNative(node.Type) || a is BigInteger n && ScalarConstants.Fits(n, ScalarConstants.NativeStorage(node.Type, 32)))) _folded[node] = a;
            return node;
        }

        private ScalarExpression EraseNative(ScalarExpression e)
        {
            var type = EraseType(e.Type);
            ScalarExpression Runtime(ScalarExpression value) => new("runtime", value.Type, value.Location, Left: value);
            if (e.Operation == "native-info")
            {
                var parts = e.Value!.Split(':'); var kind = ScalarConstants.FromSpelling(parts[1])!.Value;
                var value = parts[0] == "Size" ? _nativeTarget.Bits / 8 : parts[0] == "MinValue" ? ScalarConstants.Min(NativeStorage(kind)) : ScalarConstants.Max(NativeStorage(kind));
                return Runtime(new("literal", type, e.Location, value.ToString(CultureInfo.InvariantCulture)));
            }
            if (NativeInvolved(e) && ConstantOf(e) is { } constant)
                return new("literal", type, e.Location, constant is bool truth ? truth ? "true" : "false" : Convert.ToString(constant, CultureInfo.InvariantCulture));
            var left = e.Left is null ? null : EraseExpression(e.Left); var right = e.Right is null ? null : EraseExpression(e.Right);
            if (e.Operation is "convert" or "cast" && left is not null && left.Type == type) return left;
            // Do not let the native verifier turn a target-dependent source operation into a
            // compile-time fault or a host-width fold. Runtime wrappers retain source evaluation.
            if (NativeInvolved(e) && ConstantOf(e) is null && e.Operation is "cast" or "convert" or "unary+" or "unary-" or "unary~" or "+" or "-" or "*" or "/" or "%" or "<<" or ">>" or ">>>" or "&" or "|" or "^")
            {
                if (left is not null) left = Runtime(left);
                if (right is not null) right = Runtime(right);
            }
            return e with { Type = type, Left = left, Right = right, Arguments = e.Arguments?.Select(EraseExpression).ToList() };
        }
    }
}
