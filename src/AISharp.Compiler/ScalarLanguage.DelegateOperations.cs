namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record DelegateOperation(ScalarType Type, string Operation, string Name, SourceLocation Location)
    { public int BuiltTypeCount { get; set; } = -1; }
    [ThreadStatic] private static Dictionary<(ScalarType, string), DelegateOperation>? delegateOperations;

    private static bool EmitDelegateOperations(List<ScalarFunction> members, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> functions)
    {
        foreach (var operation in delegateOperations!.Values.ToArray())
        {
            if (operation.BuiltTypeCount == activeStructs!.Count) continue;
            var binder = new ExtendedBinder(features, "", operation.Location);
            var function = binder.BuildDelegateOperation(operation, functions);
            var old = members.FindIndex(f => f.Name == operation.Name);
            if (old < 0) members.Add(function); else members[old] = function;
            operation.BuiltTypeCount = activeStructs.Count;
        }
        return pendingInstances!.Count > 0 || pendingMethodInstances!.Count > 0;
    }

    private static bool VariantDelegateDerives(IReadOnlyList<ScalarStruct> all, int source, int target)
    {
        if (source < 0 || source >= all.Count || target < 0 || target >= all.Count || !(all[target].IsDelegate || all[target].IsDelegateFunction)) return false;
        var seen = new HashSet<int>();
        for (var current = source; current >= 0 && seen.Add(current); current = all[current].BaseIndex)
            if (VariantConstruction(all, current, target, [])) return true;
        return false;
    }

    private sealed partial class ExtendedBinder
    {
        private ScalarExpression DelegateOperationCall(ScalarType type, string operation, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            var info = DelegateOf(type);
            if (info.TypeParameters.Count == 0 || _checkOnly) return DelegateHelperCall(info, operation, [left, right], at);
            if (!delegateOperations!.TryGetValue((type, operation), out var binding))
            {
                if (delegateOperations.Count >= 256) Fail("AI3100", "More than 256 generic delegate operation bindings are not supported.", at);
                binding = new(type, operation, "__delegate_operation<" + delegateOperations.Count + ">", at); delegateOperations[(type, operation)] = binding;
            }
            return new("call", operation == "equals" ? ScalarType.Bool : type, at, binding.Name) { Arguments = [left, right] };
        }

        public ScalarFunction BuildDelegateOperation(DelegateOperation operation, IReadOnlyList<FunctionDeclaration> functions)
        {
            var at = operation.Location; var type = operation.Type; var equal = operation.Operation == "equals";
            var a = new ScalarParameter(Fresh(), type); var b = new ScalarParameter(Fresh(), type);
            var left = new ScalarExpression("local", type, at, a.Name); var right = new ScalarExpression("local", type, at, b.Name);
            ScalarExpression Null(ScalarExpression value) => new("==", ScalarType.Bool, at, Left: value, Right: new("null", type, at));
            ScalarStatement Return(ScalarExpression value) => new("return", at, value);
            List<ScalarStatement> body = equal ? [Return(Literal(false, at))] : [new("throw", at, Name: "System.ArgumentException")];
            var candidates = Enumerable.Range(0, _structs.Count).Where(i => _structs[i] is { IsDelegate: true, IsCheckInstance: false }
                && _structs[i].Template == _structs[IndexOf(type)].Template && Derives(_structs, i, IndexOf(type))).ToArray();
            foreach (var index in candidates.Reverse())
            {
                var actual = _structs[index].Id; var info = DelegateOf(actual);
                var declaration = functions.First(f => f.Name == "__" + info.DefinitionName + "_" + operation.Operation);
                var binder = new ExtendedBinder(declaration, _features, functions, new Dictionary<string, ScalarType>(), operation.Name);
                var call = binder.DelegateHelperCall(info, operation.Operation,
                    [new("downcast", actual, at, Left: left), new("downcast", actual, at, Left: right)], at);
                var success = Return(equal ? call : Upcast(call, type));
                var matchRight = new ScalarExpression("isexact", ScalarType.Bool, at, ((int)actual).ToString(System.Globalization.CultureInfo.InvariantCulture), Left: right);
                var mismatch = equal ? Return(Literal(false, at)) : new ScalarStatement("throw", at, Name: "System.ArgumentException");
                body = [new("if", at, new ScalarExpression("isexact", ScalarType.Bool, at, ((int)actual).ToString(System.Globalization.CultureInfo.InvariantCulture), Left: left),
                    Body: [new("if", at, matchRight, Body: [success], Alternative: [mismatch])], Alternative: body)];
            }
            if (equal)
            {
                body.Insert(0, new("if", at, Null(right), Body: [Return(Literal(false, at))], Alternative: []));
                body.Insert(0, new("if", at, Null(left), Body: [Return(Null(right))], Alternative: []));
            }
            else
            {
                body.Insert(0, new("if", at, Null(right), Body: [Return(left)], Alternative: []));
                body.Insert(0, new("if", at, Null(left), Body: [Return(operation.Operation == "combine" ? right : left)], Alternative: []));
            }
            var function = new ScalarFunction(operation.Name, [a, b], equal ? ScalarType.Bool : type, body, [TrueContract(at)], at);
            return Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers) ? Erase(function) : function;
        }
    }
}
