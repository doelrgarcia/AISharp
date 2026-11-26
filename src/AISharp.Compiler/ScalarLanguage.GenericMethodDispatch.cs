namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record ClosedMethodDispatch(ScalarStructMethod Root, IReadOnlyList<ScalarType> Arguments, string Name)
    { public int BuiltTypeCount { get; set; } = -1; }
    [ThreadStatic] private static Dictionary<string, ClosedMethodDispatch>? genericMethodDispatches;

    private static bool EmitGenericMethodDispatches(List<ScalarFunction> members, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> functions)
    {
        foreach (var request in genericMethodDispatches!.Values.ToArray())
        {
            if (request.BuiltTypeCount == activeStructs!.Count) continue;
            var owner = activeStructs[request.Root.Owner]; var generic = ExtendedBinder.GenericOf(request.Root.Declaration)!;
            var arguments = owner.TypeArguments is null ? new Dictionary<string, ScalarType>() : new Dictionary<string, ScalarType>(owner.TypeArguments);
            for (var i = 0; i < generic.Parameters.Count; i++) arguments[generic.Parameters[i].Name] = request.Arguments[i];
            var binder = new ExtendedBinder(request.Root.Declaration, features, functions, owner, request.Root, arguments, request.Name);
            var function = binder.ClosedGenericDispatch(request.Root, request.Arguments, request.Name);
            var old = members.FindIndex(f => f.Name == request.Name);
            if (old < 0) members.Add(function); else members[old] = function;
            request.BuiltTypeCount = activeStructs.Count;
        }
        return pendingInstances!.Count > 0 || pendingMethodInstances!.Count > 0;
    }

    private sealed partial class ExtendedBinder
    {
        private bool CallableMethod(ScalarType type, Lexeme member) => FindMethod(IndexOf(type), member.Text) is not null
            || StructOf(type).IsTypeParameter && InterfaceMethods(StructOf(type).Interfaces, member.Text).Count > 0
            || _typeArgs is not null && (!_checkOnly || !StructOf(type).IsTypeParameter) && constrainedInterfaceCalls!.ContainsKey(member.Location.Offset);

        private string QueueGenericMethod(ScalarStructMethod method, IReadOnlyList<ScalarType> arguments)
        {
            var owner = _structs[method.Owner]; var generic = GenericOf(method.Declaration)!;
            var map = owner.TypeArguments is null ? new Dictionary<string, ScalarType>() : new Dictionary<string, ScalarType>(owner.TypeArguments);
            for (var i = 0; i < generic.Parameters.Count; i++) map[generic.Parameters[i].Name] = arguments[i];
            var name = method.QualifiedName + "<" + string.Join(", ", arguments.Select(Show)) + ">";
            if (requestedInstances!.Add(name))
            {
                var current = _structs[method.Owner];
                _structs[method.Owner] = current with { Methods = [.. current.Methods, method with { Name = name, QualifiedName = name, IsLifted = true }] };
                pendingMethodInstances!.Add((owner, method, map, name));
            }
            return name;
        }

        private ScalarExpression ClosedGenericMethodCall(ScalarStructMethod method, ScalarStructMethod? selected, GenericInfo generic, IReadOnlyDictionary<string, ScalarType> map, ScalarExpression? receiver, List<ScalarExpression> arguments, ScalarType result, SourceLocation at)
        {
            var types = generic.Parameters.Select(p => map[p.Name]).ToArray();
            if (_checkOnly || map.Values.Any(IsTypeParameterType)) return ReferenceResult(new("call", result, at, method.QualifiedName) { Arguments = method.IsStatic ? arguments : [receiver!, .. arguments] }, method.Declaration.ReturnType, method.Declaration.Parameters, method.IsStatic ? 0 : 1);
            if (selected is not null)
                return InterfaceImplementationCall(selected, receiver, arguments, result, at, types);
            var root = _structs.SelectMany(t => t.Methods).First(m => !m.IsLifted && m.QualifiedName == method.Slot);
            var name = root.QualifiedName + "<" + string.Join(", ", types.Select(Show)) + ">.virtual";
            if (!genericMethodDispatches!.ContainsKey(name))
            {
                if (genericMethodDispatches.Count >= 256) Fail("AI3100", "More than 256 closed generic dispatch slots are not supported.", at);
                genericMethodDispatches[name] = new(root, types, name);
            }
            return ReferenceResult(new("call", result, at, name) { Arguments = [Upcast(receiver!, _structs[root.Owner].Id), .. arguments] }, root.Declaration.ReturnType, root.Declaration.Parameters, 1);
        }

        public ScalarFunction ClosedGenericDispatch(ScalarStructMethod root, IReadOnlyList<ScalarType> arguments, string name)
        {
            var at = root.Declaration.Location; var owner = _structs[root.Owner];
            var result = Type(root.Declaration.ReturnType, at, true); var resultMode = ParameterPassing(root.Declaration.ReturnType).Mode;
            var self = new ScalarParameter(Fresh(), owner.Id); var selfRead = new ScalarExpression("local", owner.Id, at, self.Name);
            var parameters = root.Declaration.Parameters.Select(p => SignatureParameter(p, at)).ToList();
            ScalarStatement Invoke(int type)
            {
                var implementation = owner.IsInterface ? ResolveInterfaceImplementation(type, root).Method! : FindMethod(type, root.Name)!;
                var declaring = _structs[implementation.Owner];
                var target = declaring.Id == owner.Id ? selfRead : new ScalarExpression("downcast", declaring.Id, at, Left: selfRead);
                var call = InterfaceImplementationCall(implementation, target, parameters.Select(p => ForwardParameter(p, at)).ToList(), result, at, arguments);
                return result == ScalarType.Unit ? new("expression", at, call) : Returning(call, resultMode, at);
            }
            var concrete = ConcreteOf(_structs, root.Owner).ToArray(); List<ScalarStatement> body;
            if (concrete.Length == 0) body = [new("while", at, new ScalarExpression("literal", ScalarType.Bool, at, "true"), Body: [])];
            else
            {
                body = [Invoke(concrete[^1])];
                for (var i = concrete.Length - 2; i >= 0; i--)
                    body = [new("if", at, new ScalarExpression("isexact", ScalarType.Bool, at, ((int)_structs[concrete[i]].Id).ToString(System.Globalization.CultureInfo.InvariantCulture), Left: selfRead), Body: [Invoke(concrete[i])], Alternative: body)];
            }
            var function = new ScalarFunction(name, [self, .. parameters], result, body, [TrueContract(at)], at) { ReturnMode = resultMode };
            return Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers) ? Erase(function) : function;
        }
    }
}
