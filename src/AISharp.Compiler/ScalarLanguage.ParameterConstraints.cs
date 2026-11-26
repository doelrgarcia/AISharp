namespace AISharp.Compiler;

public sealed record ScalarParameterConstraint(ScalarType Type, SourceLocation Location);
public sealed record ScalarParameterConstraintBounds(IReadOnlyList<ScalarParameterConstraint> Dependencies, int DeclaredBaseIndex, IReadOnlyList<int> DeclaredInterfaces);

public static partial class ScalarLanguage
{
    private static bool ParameterDependsOn(IReadOnlyList<ScalarStruct> all, int source, int target)
    {
        var seen = new HashSet<int>(); var pending = new Stack<int>(); pending.Push(source);
        while (pending.Count > 0)
        {
            var current = pending.Pop(); if (!seen.Add(current)) continue;
            if (current == target) return true;
            if (all[current].ParameterBounds is not { } bounds) continue;
            foreach (var dependency in bounds.Dependencies)
            {
                var index = IndexOf(dependency.Type);
                if (index >= 0 && index < all.Count) pending.Push(index);
            }
        }
        return false;
    }

    private static void ResolveParameterConstraintBounds()
    {
        var all = activeStructs!;
        foreach (var generic in typeGenerics!.Values.Concat(activeGenerics!.Values))
            foreach (var parameter in generic.Parameters)
            {
                if (parameter.Placeholder < 0) continue;
                var info = all[parameter.Placeholder]; if (info.ParameterBounds is not null || info.TypeArguments is null) continue;
                var dependencies = parameter.Constraints.Where(c => info.TypeArguments.ContainsKey(c.Text))
                    .Select(c => new ScalarParameterConstraint(info.TypeArguments[c.Text], c.Location)).ToArray();
                if (dependencies.Length > 0)
                    all[parameter.Placeholder] = info with { ParameterBounds = new(dependencies, info.BaseIndex, info.Interfaces) };
            }
        var state = new Dictionary<int, int>();
        void Apply(int index)
        {
            var info = all[index];
            if (info.ParameterBounds is not { } bounds) return;
            var baseIndex = bounds.DeclaredBaseIndex; var interfaces = bounds.DeclaredInterfaces.ToList();
            foreach (var dependency in bounds.Dependencies)
            {
                var next = IndexOf(dependency.Type);
                if (next < 0 || next >= all.Count) continue; // Substituted primitive/object bounds have no nominal declaration.
                var target = all[next];
                if (target.IsTypeParameter)
                {
                    if (target.HasValueConstraint)
                        throw DeclarationError("AI3106", "A type parameter with a struct or unmanaged constraint cannot be used as a constraint.", dependency.Location);
                }
                var inheritedBase = target.IsTypeParameter ? target.BaseIndex : target.IsClass && !target.IsInterface ? next : -1;
                if (inheritedBase >= 0)
                {
                    if (info.HasValueConstraint) throw DeclarationError("AI3106", "Dependent value-type and class constraints are inconsistent.", dependency.Location);
                    if (baseIndex < 0 || Derives(all, inheritedBase, baseIndex)) baseIndex = inheritedBase;
                    else if (!Derives(all, baseIndex, inheritedBase))
                        throw DeclarationError("AI3106", "Dependent class constraints must be related by inheritance.", dependency.Location);
                }
                if (target.IsInterface) interfaces.Add(next);
                if (target.IsTypeParameter) interfaces.AddRange(target.Interfaces);
            }
            all[index] = info with
            {
                BaseIndex = baseIndex, Interfaces = interfaces.Distinct().ToArray(),
                ReferenceKnown = info.ReferenceKnown || baseIndex >= 0,
                IsClass = !info.HasValueConstraint && (info.IsClass || baseIndex >= 0 || interfaces.Count > 0)
            };
        }
        foreach (var root in Enumerable.Range(0, all.Count).Where(i => all[i].ParameterBounds is not null))
        {
            var pending = new Stack<(int Index, bool Finish, SourceLocation At)>(); pending.Push((root, false, all[root].Location));
            while (pending.Count > 0)
            {
                var (index, finish, at) = pending.Pop();
                if (finish) { Apply(index); state[index] = 2; continue; }
                if (state.GetValueOrDefault(index) == 2) continue;
                if (state.GetValueOrDefault(index) == 1) throw DeclarationError("AI3106", "Circular type parameter constraint dependency.", at);
                state[index] = 1; pending.Push((index, true, at));
                if (all[index].ParameterBounds is not { } bounds) continue;
                foreach (var dependency in bounds.Dependencies.Reverse())
                {
                    var next = IndexOf(dependency.Type);
                    if (next >= 0 && next < all.Count && all[next].IsTypeParameter) pending.Push((next, false, dependency.Location));
                }
            }
        }
    }

    private sealed partial class ExtendedBinder
    {
        private bool ParameterValueConversion(ScalarType source, ScalarType target)
        {
            if (IsTypeParameterType(target) || IsReference(target) || target == ScalarType.String) return ParameterConstraintConversion(source, target);
            // Rechecking a closed generic owner's body preserves a conversion to its original owner parameter.
            // A concrete value-type constraint inherited by a newly declared override/explicit method grants no such conversion.
            if (!_checkOnly || _struct is not { Template: >= 0 } || _typeArgs is null) return false;
            var identities = ScopeTypeArguments(_typeArgs, _struct, _function is null ? null : GenericOf(_function));
            var seen = new HashSet<ScalarType>(); var pending = new Stack<ScalarType>(); pending.Push(source);
            while (pending.Count > 0)
            {
                var current = pending.Pop(); if (!seen.Add(current) || !IsTypeParameterType(current) || StructOf(current).ParameterBounds is not { } bounds) continue;
                foreach (var dependency in bounds.Dependencies)
                {
                    if (IsTypeParameterType(dependency.Type) && identities.TryGetValue(dependency.Type, out var actual) && actual == target) return true;
                    pending.Push(dependency.Type);
                }
            }
            return false;
        }

        private bool ParameterConstraintConversion(ScalarType source, ScalarType target)
        {
            if (IsObject(target)) return true;
            var seen = new HashSet<ScalarType>(); var pending = new Stack<ScalarType>(); pending.Push(source);
            var map = _typeArgs ?? new Dictionary<string, ScalarType>();
            var identities = ScopeTypeArguments(map, _struct, _function is null ? null : GenericOf(_function));
            while (pending.Count > 0)
            {
                var current = pending.Pop(); if (!seen.Add(current)) continue;
                if (current == target) return true;
                if (!IsStruct(current)) continue;
                var info = StructOf(current);
                if (info.IsTypeParameter)
                {
                    if (info.ParameterBounds is { } bounds)
                        foreach (var dependency in bounds.Dependencies) pending.Push(ReboundType(dependency.Type, map, dependency.Location, identities));
                    if (info.BaseIndex >= 0) pending.Push(ReboundType(_structs[info.BaseIndex].Id, map, info.Location, identities));
                    foreach (var contract in info.Interfaces) pending.Push(ReboundType(_structs[contract].Id, map, info.Location, identities));
                }
                if (!IsStruct(target) || StructOf(target).IsTypeParameter) continue;
                if ((StructOf(target).IsInterface || IsClass(current) && IsClass(target)) && Derives(_structs, IndexOf(current), IndexOf(target))) return true;
            }
            return false; // Numeric widening, user conversions and null are not constraint proofs.
        }
    }
}
