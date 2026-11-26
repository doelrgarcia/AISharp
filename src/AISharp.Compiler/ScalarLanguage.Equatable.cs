using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeEquatableName = "__native_equatable";
    public const string NativeBuiltinEquatableAdapter = "__native_equatable_builtin";

    public static bool BuiltinEquatablePayload(ScalarType type)
        => ScalarConstants.IsInteger(type) || ScalarConstants.IsFloat(type)
            || type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal or ScalarType.String;

    public static IEnumerable<int> BuiltinEquatableInterfaces(IReadOnlyList<ScalarStruct> all, ScalarType payload)
        => BuiltinEquatablePayload(payload)
            ? Enumerable.Range(0, all.Count).Where(i => IsNativeEquatable(all[i], all) && !all[i].IsCheckInstance && all[i].TypeArgumentList[0] == payload)
            : [];

    private static bool EmitBuiltinEquatableAdapters(List<ScalarFunction> members, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> functions)
    {
        var added = false;
        foreach (var owner in activeStructs!.ToArray())
            foreach (var method in owner.Methods.Where(m => m.BoxedMethodTarget == NativeBuiltinEquatableAdapter))
            {
                if (members.Any(f => f.Name == method.QualifiedName)) continue;
                members.Add(new ExtendedBinder(method.Declaration, features, functions, owner, method).BindBuiltinEquatableAdapter());
                added = true;
            }
        return added;
    }

    private static string AddEquatableSource(string source, ScalarFeatures features)
        => (features & ScalarFeatures.Generics) != 0 && new Lexer(source).Lex().Any(t => t.Kind == TokenKind.Identifier && IdentifierName(t.Text) == "IEquatable")
            ? source + "\ninterface " + NativeEquatableName + "<__native_equatable_item> { fn Equals(other: __native_equatable_item) -> bool\n}\n"
            : source;

    private static bool IsNativeEquatable(ScalarStruct owner, IReadOnlyList<ScalarStruct> all)
        => owner.IsInterface && owner.Template >= 0 && owner.Template < all.Count
            && all[owner.Template].Name == NativeEquatableName && owner.TypeArgumentList.Count == 1;

    private sealed partial class ExtendedBinder
    {
        private ScalarType? EquatableTypeText(string text, SourceLocation at, bool forceBuiltin=false)
        {
            var match = Regex.Match(text.Trim(), @"^IEquatable\s*<(.*)>$");
            if (!match.Success || !forceBuiltin && StructNamedAny("IEquatable") is not null) return null;
            var type = GenericType(NativeEquatableName + "<" + match.Groups[1].Value + ">", at);
            var argument = StructOf(type).TypeArgumentList[0];
            if (Has(ScalarFeatures.Boxing) && BuiltinEquatablePayload(argument))
                foreach (var storage in _structs.Where(t => t.BoxedType == argument || argument == ScalarType.String && t.IsStringObject).ToArray())
                    AttachBuiltinEquatable(storage);
            return type;
        }

        private bool BuiltinEquatableConversion(ScalarType payload, ScalarType target)
            => BuiltinEquatablePayload(payload) && IsStruct(target) && IsNativeEquatable(StructOf(target), _structs)
                && StructOf(target).TypeArgumentList[0] == payload;

        private bool ValueInterfaceConversion(ScalarType payload, ScalarType target)
            => IsClass(target) && StructOf(target).IsInterface && (BuiltinEquatableConversion(payload, target)
                || IsStruct(payload) && Derives(_structs, IndexOf(payload), IndexOf(target)));

        private ScalarStruct AttachBuiltinEquatable(ScalarStruct storage)
        {
            var payload = storage.BoxedType ?? (storage.IsStringObject ? ScalarType.String : ScalarType.Unit);
            if (!BuiltinEquatablePayload(payload)) return storage;
            var methods = storage.Methods.ToList();
            var interfaces = BuiltinEquatableInterfaces(_structs, payload).ToArray();
            foreach (var index in interfaces)
            {
                var root = _structs[index].Methods.Single(m => m.Name == "Equals");
                var name = storage.Name + ".$equatable_" + index;
                if (methods.Any(m => m.QualifiedName == name)) continue;
                methods.Add(new("Equals", name, true, false, root.Declaration)
                {
                    Owner = IndexOf(storage.Id), InterfaceSlot = root.Slot,
                    BoxedMethodTarget = NativeBuiltinEquatableAdapter,
                });
            }
            storage = storage with { Interfaces = interfaces, Methods = methods };
            _structs[IndexOf(storage.Id)] = storage;
            return storage;
        }

        public ScalarFunction BindBuiltinEquatableAdapter()
        {
            var owner = _struct!; var at = _function.Location;
            var payload = owner.BoxedType ?? ScalarType.String;
            var self = new ScalarParameter(Fresh(), owner.Id);
            var other = new ScalarParameter(Fresh(), payload);
            var value = new ScalarExpression("field", payload, at, "0", Left: new("local", owner.Id, at, self.Name));
            var compare = ObjectValueEqual(value, new("local", payload, at, other.Name), at);
            return Erase(new(_qualifiedName!, [self, other], ScalarType.Bool, [new("return", at, compare)], [TrueContract(at)], at));
        }

        private ScalarExpression StringToEquatable(ScalarExpression value, ScalarType target)
        {
            if (_checkOnly) return new("null", target, value.Location);
            var owner = StringObjectLayout(value.Location);
            return Upcast(new("string-object", owner.Id, value.Location, Left: value), target);
        }

        private ScalarExpression? EquatableValueEqual(ScalarExpression left, ScalarExpression right, SourceLocation at, bool liveReceiver = false)
        {
            if (!Has(ScalarFeatures.Generics) || !IsStruct(left.Type) || IsObject(left.Type)) return null;
            var owner = StructOf(left.Type);
            var contract = AssignableInterfaces(_structs, IndexOf(left.Type)).Select(i => _structs[i])
                .FirstOrDefault(t => IsNativeEquatable(t, _structs) && t.TypeArgumentList[0] == left.Type);
            if (contract is null) return null;
            var root = contract.Methods.Single(m => m.Name == "Equals");
            if (owner.IsClass)
            {
                var call = MethodCall(Upcast(left, contract.Id), new("Equals", at), given: [right]);
                return ReferenceValueEqual(left, right, at, comparison: call);
            }
            var implementation = ResolveInterfaceImplementation(IndexOf(left.Type), root).Method;
            if (implementation is null) Fail("AI3106", "The typed equality contract has no concrete implementation.", at);
            if (_structs[implementation!.Owner].IsInterface)
            {
                if (!Has(ScalarFeatures.Boxing)) Fail("AI3100", "A value-type default equality interface body requires boxing.", at);
                return MethodCall(BoxValue(left, contract.Id, receiver: liveReceiver), new("Equals", at), given: [right]);
            }
            var equal = InterfaceImplementationCall(implementation, left, [right], ScalarType.Bool, at);
            return liveReceiver || implementation.ReceiverMode == ScalarParameterMode.Value ? equal
                : equal with { Arguments = [new("argument-receiver-copy", left.Type, at, "temporary", Left: left), .. equal.Arguments!.Skip(1)] };
        }
    }
}
