namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static bool StructIndex(IReadOnlyList<ScalarStruct> all, ScalarType type, out int index)
    { index = IndexOf(type); return index >= 0 && index < all.Count && index < 1000; }

    private static bool KnownReference(IReadOnlyList<ScalarStruct> all, ScalarType type) =>
        type == ScalarType.String || (int)type == ObjectTypeId
        || StructIndex(all, type, out var index) && all[index].IsClass && (!all[index].IsTypeParameter || all[index].ReferenceKnown);

    private static IEnumerable<int> AssignableInterfaces(IReadOnlyList<ScalarStruct> all, int type)
    {
        if (all[type].IsInterface) yield return type;
        var seen = new HashSet<int>();
        for (var current = type; current >= 0 && seen.Add(current); current = all[current].BaseIndex)
            foreach (var item in InterfaceClosure(all, all[current].Interfaces)) yield return item;
    }

    private static bool ReferenceConvertible(IReadOnlyList<ScalarStruct> all, ScalarType source, ScalarType target, HashSet<(ScalarType, ScalarType)> path)
    {
        if (source == target) return true;
        if (!path.Add((source, target))) return false;
        if (StructIndex(all, source, out var a) && all[a] is { IsTypeParameter: true, ReferenceKnown: true }
            && StructIndex(all, target, out var parameter) && all[parameter].IsTypeParameter && ParameterDependsOn(all, a, parameter)) return true;
        if (!KnownReference(all, source) || !KnownReference(all, target)) return false;
        if ((int)target == ObjectTypeId) return true;
        if (!StructIndex(all, source, out a) || !StructIndex(all, target, out var b)) return false;
        if (NominalDerives(all, a, b)) return true;
        if (ArrayReferenceDerives(all, a, b, path)) return true;
        if (ArrayEnumerableDerives(all, a, b)) return true;
        if (ArrayCollectionDerives(all,a,b)) return true;
        if (all[b].IsDelegate || all[b].IsDelegateFunction) return VariantDelegateDerives(all, a, b);
        if (!all[b].IsInterface) return false;
        foreach (var candidate in AssignableInterfaces(all, a))
            if (VariantConstruction(all, candidate, b, new(path))) return true;
        return false;
    }

    private static bool VariantConstruction(IReadOnlyList<ScalarStruct> all, int source, int target, HashSet<(ScalarType, ScalarType)> path)
    {
        if (source == target) return true;
        var a = all[source]; var b = all[target];
        if (!(a.IsInterface && b.IsInterface || a.IsDelegate && b.IsDelegate || a.IsDelegateFunction && b.IsDelegateFunction)
            || a.Template < 0 || a.Template != b.Template || a.TypeArgumentList.Count != b.TypeArgumentList.Count) return false;
        for (var i = 0; i < a.TypeArgumentList.Count; i++)
        {
            var from = a.TypeArgumentList[i]; var to = b.TypeArgumentList[i]; if (from == to) continue;
            var variance = i < a.TypeVariances.Count ? a.TypeVariances[i] : 0;
            if (variance == 0 || !(variance > 0 ? ReferenceConvertible(all, from, to, new(path)) : ReferenceConvertible(all, to, from, new(path)))) return false;
        }
        return true;
    }

    private static bool VariantInterfaceDerives(IReadOnlyList<ScalarStruct> all, int source, int target)
    {
        if (source < 0 || source >= all.Count || target < 0 || target >= all.Count || !all[target].IsInterface) return false;
        return AssignableInterfaces(all, source).Any(candidate => VariantConstruction(all, candidate, target, []));
    }

    private static void CheckVarianceSafety(ScalarStruct owner, string text, int position, SourceLocation at, IReadOnlySet<string>? shadowed = null)
    {
        var passing = ParameterPassing(text);
        if (passing.Mode != ScalarParameterMode.Value) { text = passing.Element; position = 0; }
        if (!owner.TypeVariances.Any(v => v != 0)) return;
        var pending = new Stack<(string Text, int Polarity, int Depth)>(); pending.Push((text, position, 0));
        while (pending.Count > 0)
        {
            var (value, polarity, depth) = pending.Pop(); var type = value.Trim();
            if (depth > 256) throw DeclarationError("AI3100", "Variance type nesting exceeds the generic instantiation depth profile.", at);
            if (type.EndsWith("[]", StringComparison.Ordinal)) { pending.Push((type[..^2], polarity, depth + 1)); continue; }
            if (shadowed?.Contains(type) == true) continue;
            var index = owner.TypeParameters.ToList().IndexOf(type);
            if (index >= 0)
            {
                if (owner.TypeVariances[index] != 0 && owner.TypeVariances[index] != polarity)
                    throw DeclarationError("AI3106", $"Type '{text}' is not variance-safe in this {(position > 0 ? "output" : "input")} position.", at);
                continue;
            }
            var open = type.IndexOf('<'); if (open < 0 || !type.EndsWith('>')) continue;
            var declaration = activeStructs!.FirstOrDefault(s => s.Name == type[..open].Trim() && (s.Template < 0 || s.Template == IndexOf(s.Id)));
            var arguments = SplitTypeList(type[(open + 1)..^1]).ToArray();
            for (var i = 0; i < arguments.Length; i++)
            {
                var variance = declaration is not null && (declaration.IsInterface || declaration.IsDelegate) && i < declaration.TypeVariances.Count ? declaration.TypeVariances[i] : 0;
                pending.Push((arguments[i], polarity == 0 || variance == 0 ? 0 : polarity * variance, depth + 1));
            }
        }
    }

    private static void ValidateVarianceBases()
    {
        for (var i = 0; i < activeStructs!.Count; i++)
            if (activeStructs[i] is { IsInterface: true } owner && owner.Template == i && owner.TypeVariances.Any(v => v != 0))
                foreach (var item in genericBases!.GetValueOrDefault(i, [])) CheckVarianceSafety(owner, item.Name, 1, item.Location);
    }

    private sealed partial class ExtendedBinder
    {
        private ScalarExpression InterfaceImplementationCall(ScalarStructMethod method, ScalarExpression? receiver, List<ScalarExpression> arguments, ScalarType result, SourceLocation at, IReadOnlyList<ScalarType>? methodArguments = null)
        {
            var owner = _structs[method.Owner];
            var map = owner.TypeArguments is null ? new Dictionary<string, ScalarType>() : new Dictionary<string, ScalarType>(owner.TypeArguments);
            if (methodArguments is not null)
            {
                var generic = GenericOf(method.Declaration)!;
                for (var i = 0; i < generic.Parameters.Count; i++) map[generic.Parameters[i].Name] = methodArguments[i];
            }
            for (var i = 0; i < arguments.Count; i++) arguments[i] = Coerce(arguments[i], MemberType(map, method.Declaration.Parameters[i].Type, at));
            var actual = MemberType(map, method.Declaration.ReturnType, at, true);
            var name = methodArguments is null ? method.QualifiedName : QueueGenericMethod(method, methodArguments);
            var call = ReferenceResult(new("call", actual, at, name) { Arguments = method.IsStatic ? arguments : [AccessorReceiverArgument(method, receiver!, at), .. arguments] }, method.Declaration.ReturnType, method.Declaration.Parameters, method.IsStatic ? 0 : 1);
            return Coerce(AccessorReferenceResult(call, method), result);
        }
    }
}
