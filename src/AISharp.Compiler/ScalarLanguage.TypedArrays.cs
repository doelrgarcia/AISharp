using System.Globalization;
using System.Numerics;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeArrayPrefix = "__native_array_";
    [ThreadStatic] private static bool activeTypedArrayFaults;
    private static bool TypedArrayFaultsRequested(string source, bool arraySyntaxEnabled)
    {
        var tokens = new Lexer(source).Lex();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Identifier) continue;
            if (tokens[i].Text == "ArrayTypeMismatchException") return true;
            if (arraySyntaxEnabled && tokens[i].Text != "string" && i + 2 < tokens.Count && tokens[i + 1].Text == "["
                && (tokens[i + 2].Text == "]" || i > 0 && tokens[i - 1].Text == "new")) return true;
        }
        return false;
    }
    private static ScalarType? ArrayElementIdentity(IReadOnlyList<ScalarStruct> all, int index)
        => index >= 0 && index < all.Count ? all[index].IsStringArray ? ScalarType.String : all[index].ArrayElementType : null;
    private static bool ArrayReferenceDerives(IReadOnlyList<ScalarStruct> all, int source, int target, HashSet<(ScalarType, ScalarType)>? path = null)
        => ArrayElementIdentity(all, source) is { } from && ArrayElementIdentity(all, target) is { } to
            && KnownReference(all, from) && KnownReference(all, to) && ReferenceConvertible(all, from, to, path is null ? [] : new(path));
    private sealed partial class ExtendedBinder
    {
        private bool IsTypedArray(ScalarType type) => Has(ScalarFeatures.TypedArrays) && IsStruct(type) && StructOf(type).ArrayElementType is not null;
        private bool IsArray(ScalarType type) => IsStringArray(type) || IsTypedArray(type);
        private ScalarType ArrayElement(ScalarType type) => IsStringArray(type) ? ScalarType.String : StructOf(type).ArrayElementType!.Value;
        private bool ArrayTypeAhead(int offset = 0)
        {
            if (StringArrayTypeAhead(offset)) return true;
            if (!Has(ScalarFeatures.TypedArrays)) return false;
            var saved = _position; var originalTokens = _tokens; _tokens = [.. _tokens];
            try
            {
                _position += offset;
                return TypeToken().Text.EndsWith("[]", StringComparison.Ordinal);
            }
            catch (ScalarError) { return false; }
            finally { _position = saved; _tokens = originalTokens; }
        }
        private bool TypedArrayCastAt()
        {
            if (!Has(ScalarFeatures.TypedArrays) || Current.Text != "(") return false;
            var saved = _position; var originalTokens = _tokens; _tokens = [.. _tokens];
            try
            {
                Next(); var token = TypeToken();
                return token.Text.EndsWith("[]", StringComparison.Ordinal) && Current.Text == ")";
            }
            catch (ScalarError) { return false; }
            finally { _position = saved; _tokens = originalTokens; }
        }
        private ScalarExpression TypedArrayCast()
        {
            Next(); var token = TypeToken(); Expect(")"); var operand = Unary(); Read(operand);
            return ReferenceCast(Type(token.Text, token.Location), operand, token.Location);
        }
        private (ScalarStatement Statement, bool Returns) ForeachTypedArray(SourceLocation at, Lexeme name, Lexeme? typeToken, ScalarExpression source)
        {
            var before = new HashSet<string>(_assigned); var array = HiddenLocal(source.Type);
            var index = new Local($"__scalar_local_{_nextId++}", ScalarType.Int, true); _hiddenLocals[index.EmittedName] = index; _assigned.Add(index.EmittedName);
            var readArray = new ScalarExpression("local", source.Type, at, array.EmittedName);
            var readIndex = new ScalarExpression("local", ScalarType.Int, at, index.EmittedName);
            var start = new ScalarStatement("local", at, new("literal", ScalarType.Int, at, "0"), index.EmittedName, ScalarType.Int) { Mutable = true };
            var length = new ScalarExpression("field", ScalarType.Int, at, "0", Left: readArray);
            var condition = new ScalarExpression("<", ScalarType.Bool, at, Left: readIndex, Right: length);
            var step = Lower(new("post++", ScalarType.Int, at, Left: readIndex) { Wraps = _wraps });
            var elementType = ArrayElement(source.Type); var variableType = typeToken is null ? elementType : Type(typeToken.Text, typeToken.Location);
            var itemValue = new ScalarExpression("array-index", elementType, at, Left: readArray, Right: readIndex);
            var converted = TryCoerce(itemValue, variableType);
            if (converted is null && IsNum(elementType) && IsNum(variableType)) converted = Fold(new("cast", variableType, at, Left: itemValue) { Wraps = _wraps });
            if (converted is null && (IsReference(elementType) || IsReference(variableType))) converted = ReferenceCast(variableType, itemValue, at);
            if (converted is null) Fail("AI3107", "The array element cannot be converted to the foreach variable type.", typeToken?.Location ?? name.Location);
            _scopes.Add(new(StringComparer.Ordinal)); var element = Declare(name.Text, variableType, false, name.Location); _assigned.Add(element.EmittedName);
            var flags = new LoopFlags(); _loops.Push(flags); var body = Block(); _loops.Pop();
            foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
            _scopes.RemoveAt(_scopes.Count - 1); _assigned = new(before); _assigned.Add(array.EmittedName); _assigned.Add(index.EmittedName);
            var item = new ScalarStatement("local", at, converted!, element.EmittedName, variableType) { Mutable = false };
            var loop = new ScalarStatement("for", at, condition, Body: [item, .. body.Body], Initializer: start, Increment: step);
            return (new("block", at, Body: [new("local", at, source, array.EmittedName, source.Type) { Mutable = false }, loop]), false);
        }
        private ScalarType TypedArray(ScalarType element, SourceLocation at)
        {
            if (IsObject(element) && Has(ScalarFeatures.Boxing) && !_checkOnly && _structs.Any(t => t.IsStringArray)) _ = StringObjectLayout(at);
            if (_structs.FirstOrDefault(t => t.ArrayElementType == element) is { } known) return known.Id;
            if (IsStruct(element) && StructOf(element).IsStatic) Fail("AI3106", "A static type cannot be an array element.", at);
            if (element == ScalarType.Unit || IsObject(element) == false && !IsStruct(element) && !IsEnum(element)
                && !ScalarConstants.IsInteger(element) && !ScalarConstants.IsFloat(element) && element is not (ScalarType.Bool or ScalarType.Char or ScalarType.String or ScalarType.Decimal))
                Fail("AI3106", "An array requires a supported storage element type.", at);
            if (_structs.Count(t => t.ArrayElementType is not null) >= 128 || _structs.Count >= 1000)
                Fail("AI3100", "The native typed-array layout budget is exhausted.", at);
            var index = _structs.Count; var id = (ScalarType)(StructTypeBase + index);
            var placeholder = IsStruct(element) && StructOf(element) is { } info && (info.IsTypeParameter || info.IsCheckInstance)
                ||IsEnum(element)&&EnumOf(element).IsCheckInstance;
            _structs.Add(new(NativeArrayPrefix + ((int)element).ToString(CultureInfo.InvariantCulture), id,
                [new("Length", ScalarType.Int, ScalarType.Int, true, false, at) { Owner = index, IsReadonly = true }], [], at, IsClass: true)
                { IsSealed = true, ArrayElementType = element, IsCheckInstance = placeholder });
            return id;
        }
        private ScalarExpression NewTypedArray(Lexeme keyword, Lexeme typeToken)
        {
            var elementText = typeToken.Text; var empty = elementText.EndsWith("[]", StringComparison.Ordinal);
            if (empty) elementText = elementText[..^2];
            var element = Type(elementText, typeToken.Location); ScalarExpression? count = null;
            if (!empty)
            {
                Expect("[");
                if (!Take("]")) { count = Expression(); Read(count); count = Coerce(count, ScalarType.Int); Expect("]"); }
                // Only the outer rank has a length in a jagged creation. Each
                // following empty rank belongs to the element's array type.
                while (Take("["))
                {
                    Expect("]"); elementText += "[]";
                    element = Type(elementText, typeToken.Location);
                }
            }
            var values = new List<ScalarExpression>();
            if (Take("{"))
            {
                NewLines();
                while (Current.Text != "}")
                {
                    var value = Expression(); Read(value); values.Add(Coerce(value, element)); NewLines();
                    if (!Take(",")) break; NewLines();
                }
                Expect("}");
                if (count is not null && (ConstantOf(count) is not BigInteger length || length != values.Count))
                    Fail("AI3106", "An array initializer requires a matching constant length.", count.Location);
                count ??= new("literal", ScalarType.Int, keyword.Location, values.Count.ToString(CultureInfo.InvariantCulture));
            }
            if (count is null) Fail("AI3106", "An array creation needs a length or an initializer.", typeToken.Location);
            if (ConstantOf(count!) is BigInteger negative && negative.Sign < 0) Fail("AI3112", "An array length cannot be a negative constant.", count!.Location);
            // Resolve the complete type so string[] keeps its established layout
            // and the outer rank participates in the array-depth budget.
            return new("array-new", Type(elementText + "[]", typeToken.Location), keyword.Location, Left: count) { Arguments = values };
        }
    }
}
