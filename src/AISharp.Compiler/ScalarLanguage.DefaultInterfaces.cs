namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<int, ScalarStructMethod>? constrainedInterfaceCalls;
    [ThreadStatic] private static bool activeDefaultInterfaces;
    private static IEnumerable<int> ImplementedInterfaces(int type)
    {
        var all = activeStructs!; var seen = new HashSet<int>();
        for (var current = type; current >= 0; current = all[current].BaseIndex)
            foreach (var item in InterfaceClosure(all, all[current].Interfaces)) if (seen.Add(item)) yield return item;
    }

    private sealed record InterfaceResolution(ScalarStructMethod? Method, bool Ambiguous = false);

    private static InterfaceResolution ResolveInterfaceImplementation(int type, ScalarStructMethod root)
    {
        var all = activeStructs!;
        if (!NominalDerives(all, type, root.Owner))
            foreach (var candidate in AssignableInterfaces(all, type).Distinct())
            {
                if (!VariantConstruction(all, candidate, root.Owner, [])) continue;
                var mapped = all[candidate].Methods.FirstOrDefault(m => !m.IsLifted && m.InterfaceQualifier is null && m.Declaration.Location.Offset == root.Declaration.Location.Offset);
                if (mapped is not null) return ResolveInterfaceImplementation(type, mapped);
            }
        var introduction = all[type].IsInterface ? -1 : type;
        while (introduction >= 0 && !InterfaceClosure(all, all[introduction].Interfaces).Contains(root.Owner)) introduction = all[introduction].BaseIndex;
        for (var current = introduction; current >= 0; current = all[current].BaseIndex)
        {
            var method = all[current].Methods.FirstOrDefault(m => m.InterfaceSlot == root.Slot)
                ?? all[current].Methods.FirstOrDefault(m => m.InterfaceQualifier is null && !m.IsConstructor && (root.IsAccessor ? SameAccessor(m, root) : !m.IsAccessor && m.Name == root.Name) && m.IsPublic && m.IsStatic == root.IsStatic && SameSignature(m.Declaration, root.Declaration));
            if (method is null) continue;
            CheckGenericMethodConstraints(method, root);
            if (!root.IsStatic && method.Slot is { } slot)
                for (var derived = type; derived >= 0; derived = all[derived].BaseIndex)
                    if (all[derived].Methods.FirstOrDefault(m => m.Slot == slot && m.InterfaceQualifier is null) is { } implementation) return new(implementation);
            return new(method);
        }
        var candidates = new List<ScalarStructMethod> { root };
        candidates.AddRange((all[type].IsInterface ? new[] { type }.Concat(ImplementedInterfaces(type)) : ImplementedInterfaces(type)).SelectMany(index => all[index].Methods).Where(method => method.InterfaceSlot == root.Slot));
        var mostSpecific = candidates.DistinctBy(m => m.QualifiedName).Where(method => !candidates.Any(other => other.Owner != method.Owner && NominalDerives(all, other.Owner, method.Owner))).ToArray();
        return mostSpecific.Length != 1 ? new(null, true) : new(mostSpecific[0].IsAbstract ? null : mostSpecific[0]);
    }

    private static void ResolveExplicitInterfaceMembers()
    {
        var all = activeStructs!;
        for (var index = 0; index < all.Count; index++)
        {
            var owner = all[index]; var methods = owner.Methods.ToList();
            for (var i = 0; i < methods.Count; i++)
            {
                var method = methods[i]; if (method.InterfaceQualifier is not { } qualifier) continue;
                var target = all.FindIndex(t => t.IsInterface && t.Name == qualifier);
                if (qualifier.Contains('<'))
                {
                    var binder = new ExtendedBinder(activeFeatures, "", method.Declaration.Location);
                    if (owner.TypeArguments is not null) binder.UseTypeArguments(new Dictionary<string, ScalarType>(owner.TypeArguments), owner.IsCheckInstance);
                    var type = binder.HierarchyType(qualifier, method.Declaration.Location); target = IndexOf(type);
                    if (target < 0 || target >= all.Count || !all[target].IsInterface) target = -1;
                }
                if (target < 0) throw DeclarationError("AI3106", $"'{qualifier}' is not an interface.", method.Declaration.Location);
                if (!InterfaceClosure(all, owner.Interfaces).Contains(target)) throw DeclarationError("AI3106", $"'{owner.Name}' does not directly implement or inherit interface '{qualifier}'.", method.Declaration.Location);
                var name = method.Name[(method.Name.LastIndexOf('.') + 1)..];
                var root = all[target].Methods.FirstOrDefault(m => m.InterfaceQualifier is null && (method.IsAccessor ? SameAccessor(m,method) : m.Name == name));
                if (root is null || root.Slot is null || !root.IsPublic || root.IsStatic != method.IsStatic || !SameSignature(method.Declaration, root.Declaration))
                    throw DeclarationError("AI3106", $"'{method.Name}' does not name a matching virtual member declared in '{qualifier}'.", method.Declaration.Location);
                if (method.IsAccessor) CheckReferenceScopeContract(method,root);
                methods[i] = method with { InterfaceSlot = root.Slot, Slot = owner.IsInterface ? root.Slot : null };
            }
            all[index] = owner with { Methods = methods };
        }
    }

    private sealed partial class ExtendedBinder
    {
        private bool QualifiedInterfaceMemberFrom(Lexeme first)
        {
            _position--; try { return QualifiedInterfaceMemberAt(); } finally { _position++; }
        }

        private void BindInterfaceMember(ScalarStruct header, IReadOnlySet<string> modifiers, bool? visibility, List<MemberSpan> methods, SourceLocation? asyncLocation)
        {
            var fn = Next(); var first = Current; var name = Next(); string? qualifier = null;
            if (Current.Text == "<" && QualifiedInterfaceMemberFrom(first))
            {
                _position--; var qualified = HeaderTypeToken(); Expect("."); qualifier = qualified.Text; name = Next();
            }
            else if (Take(".")) { qualifier = first.Text; name = Next(); }
            if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || name.Text is "this" or "new") Fail("AI3106", $"Invalid member name '{name.Text}'.", name.Location);
            var generic = BindMemberTypeParameters(name);
            if (qualifier is not null && (visibility is not null || modifiers.Any(m => m != "abstract" && m != "async" && m != "static"))) Fail("AI3106", "An explicit interface implementation takes no access, virtual or sealed modifier.", first.Location);
            var parameters = new List<ParameterDeclaration>(); var parameterTypes = new List<Lexeme>(); Expect("(");
            if (!Take(")"))
            {
                do { var parameter = Next(); if (!IsSourceIdentifier(parameter.Text) || IsReserved(parameter.Text)) Fail("AI3106", $"Invalid parameter name '{parameter.Text}'.", parameter.Location); Expect(":"); var type = ParameterTypeToken(); parameters.Add(new(parameter.Text, type.Text)); parameterTypes.Add(type); } while (Take(","));
                Expect(")");
            }
            Expect("-"); Expect(">"); var result = ParameterTypeToken();
            if (generic is not null) BindTypeConstraints(generic);
            if (!modifiers.Contains("static") || modifiers.Contains("abstract") || modifiers.Contains("virtual"))
            {
                var shadowed = generic?.Parameters.Select(p => p.Name).ToHashSet() ?? [];
                foreach (var type in parameterTypes) CheckVarianceSafety(header, type.Text, -1, type.Location, shadowed);
                CheckVarianceSafety(header, result.Text, 1, result.Location, shadowed);
                if (generic is not null) foreach (var parameter in generic.Parameters)
                    foreach (var constraint in parameter.Constraints) CheckVarianceSafety(header, constraint.Text, -1, constraint.Location, shadowed);
            }
            var afterSignature = _position;
            var semicolon = Take(";"); NewLines(); var hasBody = !semicolon && (Current.Text == "{" || Current.Text is "ensures" or "requires");
            var actualName = qualifier is null ? name.Text : qualifier + "." + name.Text;
            if (!hasBody)
            {
                if (modifiers.Contains("static") && !modifiers.Contains("abstract")) Fail("AI3106", "A static interface signature requires abstract or an implementation body.", name.Location);
                if (modifiers.Contains("async")) Fail("AI3106", "An async member needs an implementation body and cannot be abstract or an interface signature.", asyncLocation ?? name.Location);
                if (modifiers.Contains("virtual") || modifiers.Contains("sealed") || modifiers.Contains("async") || visibility == false) Fail("AI3106", "This interface member requires an implementation body.", name.Location);
                var signature = new FunctionDeclaration(name.Name, parameters, result.Text, [], [], new FunctionBody([]) { ScalarSource = new("", name.Location) }, [], name.Location);
                methods.Add(new(actualName, name.Location.Offset, fn.Location.Offset, fn.Location.Offset, qualifier is null, modifiers, signature) { InterfaceQualifier = qualifier, QualifierStart = first.Location.Offset });
                return;
            }
            if (modifiers.Contains("abstract")) Fail("AI3106", "An abstract interface member cannot have a body.", name.Location);
            if (modifiers.Contains("virtual") && modifiers.Contains("sealed")) Fail("AI3106", "An interface member cannot be both virtual and sealed.", name.Location);
            if (visibility == false && (modifiers.Contains("virtual") || modifiers.Contains("sealed"))) Fail("AI3106", "A private interface helper cannot be virtual or sealed.", name.Location);
            _position = afterSignature; var depth = 0; Lexeme? close = null;
            while (Current.Text != "") { var token = Next(); if (token.Text == "{") depth++; else if (token.Text == "}" && --depth == 0) { close = token; break; } }
            if (close is null) Fail("AI3106", "Unterminated interface method body.", fn.Location);
            methods.Add(new(actualName, name.Location.Offset, fn.Location.Offset, close!.Location.Offset + 1, qualifier is null && visibility != false, modifiers, null) { InterfaceQualifier = qualifier, QualifierStart = first.Location.Offset });
        }
    }
}
