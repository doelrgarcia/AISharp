namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record MethodConstraintSource(ScalarStructMethod Implementation, ScalarStructMethod Root);
    [ThreadStatic] private static Dictionary<int, MethodConstraintSource>? genericConstraintSources;

    private static string MethodSignatureType(FunctionDeclaration declaration, string text)
    {
        var importedShadows=ExtendedBinder.GenericOf(declaration)?.Parameters.Select(p=>p.Name).ToHashSet(StringComparer.Ordinal)??new HashSet<string>(StringComparer.Ordinal);
        if(templateDeclarations is not null)foreach(var owner in templateDeclarations.Where(p=>declaration.Location.Offset>=p.Value.Start.Offset&&declaration.Location.Offset<p.Value.Start.Offset+p.Value.Text.Length))
            importedShadows.UnionWith(activeStructs![owner.Key].TypeParameters);
        text=ImportSignatureSyntax(text,importedShadows);
        var qualifiedNames=new List<string>();
        text=System.Text.RegularExpressions.Regex.Replace(text,@"(?:global::)?"+IdentifierSpelling.Pattern+@"(?:\."+IdentifierSpelling.Pattern+@")+",m=>
        {
            if(QualifiedBuiltinAlias(m.Value) is not {} alias)return m.Value;
            qualifiedNames.Add(alias);return "\u0001"+(qualifiedNames.Count-1).ToString(System.Globalization.CultureInfo.InvariantCulture)+"\u0001";
        });
        if (ScopedReference(text)) text = text.TrimStart()[7..];
        var passing = ParameterPassing(text);
        if (passing.Mode == ScalarParameterMode.RefReadonly) text = "in " + passing.Element;
        var generic = ExtendedBinder.GenericOf(declaration);
        if (generic is not null)
            text = RewriteTypeIdentifiers(text, name =>
            { var index = generic.Parameters.FindIndex(p => p.Name == name); return index < 0 ? null : "~" + index; });
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\$([0-9]+)", match =>
        {
            var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - StructTypeBase;
            return index >= 0 && index < activeStructs!.Count ? activeStructs[index].Name : match.Value;
        });
        text=System.Text.RegularExpressions.Regex.Replace(text,@"\u0001([0-9]+)\u0001",m=>qualifiedNames[int.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)]);
        return SignatureType(text);
    }

    private static void DiscoverGenericConstraintSources()
    {
        var all = activeStructs!;
        for (var owner = 0; owner < all.Count; owner++)
        {
            var type = all[owner]; if (type.Template >= 0 && type.Template != owner) continue;
            foreach (var method in type.Methods)
            {
                if (ExtendedBinder.GenericOf(method.Declaration) is null) continue;
                ScalarStructMethod? root = null;
                if (method.IsOverride && type.BaseIndex >= 0) root = FindMethod(type.BaseIndex, method.Name);
                else if (method.InterfaceQualifier is { } qualifier)
                {
                    var binder = new ExtendedBinder(activeFeatures, "", method.Declaration.Location);
                    if (type.TypeArguments is not null) binder.UseTypeArguments(new Dictionary<string, ScalarType>(type.TypeArguments), type.IsCheckInstance);
                    var target = binder.HierarchyType(qualifier, method.Declaration.Location); var index = IndexOf(target);
                    if (index >= 0 && index < all.Count && all[index].IsInterface)
                    {
                        var name = method.Name[(method.Name.LastIndexOf('.') + 1)..];
                        root = all[index].Methods.FirstOrDefault(m => m.InterfaceQualifier is null && m.Name == name);
                    }
                }
                if (root is not null && ExtendedBinder.GenericOf(root.Declaration) is not null && SameSignature(method.Declaration, root.Declaration))
                    genericConstraintSources![method.Declaration.Location.Offset] = new(method, root);
            }
        }
    }

    private static void InheritGenericMethodConstraints()
    {
        var all = activeStructs!; var visiting = new HashSet<int>(); var done = new HashSet<int>();
        void Inherit(int offset)
        {
            if (done.Contains(offset) || !genericConstraintSources!.TryGetValue(offset, out var relation)) return;
            if (!visiting.Add(offset)) throw DeclarationError("AI3106", "Circular generic method constraint inheritance.", relation.Implementation.Declaration.Location);
            Inherit(relation.Root.Declaration.Location.Offset);
            var source = ExtendedBinder.GenericOf(relation.Root.Declaration)!; var target = ExtendedBinder.GenericOf(relation.Implementation.Declaration)!;
            var owner = all[relation.Root.Owner]; var binder = new ExtendedBinder(activeFeatures, "", relation.Implementation.Declaration.Location);
            for (var i = 0; i < target.Parameters.Count; i++)
            {
                var parameter = target.Parameters[i]; var proof = all[source.Parameters[i].Placeholder];
                foreach (var constraint in parameter.Constraints)
                    if (constraint.Text != "class" && constraint.Text != "struct" || constraint.Text == "class" && !proof.ReferenceKnown || constraint.Text == "struct" && !proof.HasValueConstraint)
                        throw DeclarationError("AI3106", "An override or explicit implementation inherits its generic constraints; only a matching class or struct primary constraint can be repeated.", constraint.Location);
                var own = all[parameter.Placeholder];
                var inherited = binder.InheritedMethodConstraint(proof, owner, source, target, relation.Implementation.Declaration.Location);
                all[parameter.Placeholder] = inherited with { Name = own.Name, Id = own.Id, Location = own.Location, TypeArguments = own.TypeArguments };
            }
            visiting.Remove(offset); done.Add(offset);
        }
        foreach (var offset in genericConstraintSources!.Keys.ToArray()) Inherit(offset);
    }

    private static void CheckGenericMethodConstraints(ScalarStructMethod implementation, ScalarStructMethod root)
    {
        CheckReferenceScopeContract(implementation, root);
        var expected = ExtendedBinder.GenericOf(root.Declaration); var actual = ExtendedBinder.GenericOf(implementation.Declaration);
        if (expected is null || actual is null || implementation.InterfaceQualifier is not null) return;
        var binder = new ExtendedBinder(activeFeatures, "", implementation.Declaration.Location);
        if (!binder.EquivalentMethodConstraints(root, implementation))
            throw DeclarationError("AI3106", $"The generic constraints of '{implementation.QualifiedName}' must match '{root.QualifiedName}'.", implementation.Declaration.Location);
    }

    private sealed partial class ExtendedBinder
    {
        private GenericInfo? BindMemberTypeParameters(Lexeme name)
        {
            if (!Take("<")) return null;
            if (!Has(ScalarFeatures.Generics)) Fail("AI3100", "Generic methods are disabled.", name.Location);
            var generic = new GenericInfo(name.Name, name.Location.Offset, []);
            do
            {
                var parameter = Next();
                if (!ValidTypeParameterName(parameter.Text))
                    Fail("AI3106", $"Invalid type parameter name '{parameter.Text}'.", parameter.Location);
                if (generic.Parameters.Any(p => p.Name == parameter.Name)) Fail("AI3106", $"Duplicate type parameter '{parameter.Text}'.", parameter.Location);
                generic.Parameters.Add(new(parameter.Name, parameter.Location));
            } while (Take(","));
            Expect(">");
            if (!activeGenerics!.ContainsKey(generic.NameOffset)) activeGenerics[generic.NameOffset] = generic;
            return generic;
        }

        public ScalarStruct InheritedMethodConstraint(ScalarStruct proof, ScalarStruct owner, GenericInfo source, GenericInfo target, SourceLocation at)
        {
            var identities = ScopeTypeArguments(new Dictionary<string, ScalarType>(), owner, null);
            for (var i = 0; i < source.Parameters.Count; i++) identities[(ScalarType)(StructTypeBase + source.Parameters[i].Placeholder)] = (ScalarType)(StructTypeBase + target.Parameters[i].Placeholder);
            return ReboundConstraint(proof, new Dictionary<string, ScalarType>(), at, identities);
        }

        public bool EquivalentMethodConstraints(ScalarStructMethod first, ScalarStructMethod second)
        {
            var a = GenericOf(first.Declaration)!; var b = GenericOf(second.Declaration)!;
            string Key(ScalarType type, ScalarStructMethod method, GenericInfo generic, HashSet<ScalarType> seen)
            {
                var ordinal = generic.Parameters.FindIndex(p => StructTypeBase + p.Placeholder == (int)type);
                if (ordinal >= 0) return "~" + ordinal;
                if (!IsStruct(type)) return "v" + (int)type;
                var info = StructOf(type); var owner = _structs[method.Owner];
                if (info.IsTypeParameter && owner.Template >= 0 && _structs[owner.Template].TypeArguments is { } original)
                    foreach (var argument in original)
                        if (argument.Value == type && owner.TypeArguments![argument.Key] != type && seen.Add(type)) return Key(owner.TypeArguments[argument.Key], method, generic, seen);
                return info.Template < 0 ? "v" + (int)type : "g" + info.Template + "<" + string.Join(",", info.TypeArgumentList.Select(t => Key(t, method, generic, new(seen)))) + ">";
            }
            var firstProofs = NormalizedMethodConstraints(first, a); var secondProofs = NormalizedMethodConstraints(second, b);
            for (var i = 0; i < a.Parameters.Count; i++)
            {
                var x = firstProofs[a.Parameters[i].Placeholder]; var y = secondProofs[b.Parameters[i].Placeholder];
                if ((x.ReferenceKnown, x.HasValueConstraint, x.HasUnmanagedConstraint, x.HasConstructorConstraint) != (y.ReferenceKnown, y.HasValueConstraint, y.HasUnmanagedConstraint, y.HasConstructorConstraint)) return false;
                var xb = x.BaseIndex < 0 ? "" : Key(_structs[x.BaseIndex].Id, first, a, []); var yb = y.BaseIndex < 0 ? "" : Key(_structs[y.BaseIndex].Id, second, b, []);
                var xd = x.ParameterBounds?.Dependencies.Select(d => Key(d.Type, first, a, [])).Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>();
                var yd = y.ParameterBounds?.Dependencies.Select(d => Key(d.Type, second, b, [])).Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>();
                if (!xd.SequenceEqual(yd)) return false;
                if (xb != yb || !x.Interfaces.Select(j => Key(_structs[j].Id, first, a, [])).Order(StringComparer.Ordinal).SequenceEqual(y.Interfaces.Select(j => Key(_structs[j].Id, second, b, [])).Order(StringComparer.Ordinal))) return false;
            }
            return true;
        }

        private IReadOnlyDictionary<int, ScalarStruct> NormalizedMethodConstraints(ScalarStructMethod method, GenericInfo generic)
        {
            var owner = _structs[method.Owner];
            var map = owner.TypeArguments is null ? new Dictionary<string, ScalarType>() : new Dictionary<string, ScalarType>(owner.TypeArguments);
            foreach (var item in generic.Parameters) map[item.Name] = _structs[item.Placeholder].Id;
            var identities = ScopeTypeArguments(map, owner, generic); var normalized = new Dictionary<int, ScalarStruct>(); var visiting = new HashSet<int>();
            var pending = new Stack<(int Index, bool Finish)>(generic.Parameters.AsEnumerable().Reverse().Select(p => (p.Placeholder, false)));
            ScalarStruct Rebound(int index) => ReboundConstraint(_structs[index], map, method.Declaration.Location, identities);
            while (pending.Count > 0)
            {
                var (index, finish) = pending.Pop(); if (normalized.ContainsKey(index)) continue;
                var proof = Rebound(index);
                if (!finish)
                {
                    if (!visiting.Add(index)) continue;
                    pending.Push((index, true));
                    if (proof.ParameterBounds is { } dependencies)
                        foreach (var edge in dependencies.Dependencies.Reverse())
                            if (IsTypeParameterType(edge.Type) && IndexOf(edge.Type) != index && !visiting.Contains(IndexOf(edge.Type))) pending.Push((IndexOf(edge.Type), false));
                    continue;
                }
                var basis = proof.ParameterBounds?.DeclaredBaseIndex ?? proof.BaseIndex;
                var interfaces = (proof.ParameterBounds?.DeclaredInterfaces ?? proof.Interfaces).ToList(); var retained = new List<ScalarParameterConstraint>();
                void AddBase(int next)
                {
                    if (next < 0) return;
                    if (basis < 0 || NominalDerives(_structs, next, basis)) basis = next;
                    else if (!NominalDerives(_structs, basis, next)) throw DeclarationError("AI3106", "Substituted method constraints have unrelated class bounds.", method.Declaration.Location);
                }
                if (proof.ParameterBounds is { } bounds) foreach (var edge in bounds.Dependencies)
                {
                    if (IsObject(edge.Type) || edge.Type == proof.Id) continue;
                    if (!IsStruct(edge.Type)) { retained.Add(edge); continue; }
                    var target = StructOf(edge.Type);
                    if (target.IsTypeParameter)
                    {
                        retained.Add(edge); target = normalized.GetValueOrDefault(IndexOf(edge.Type), Rebound(IndexOf(edge.Type)));
                        AddBase(target.BaseIndex); interfaces.AddRange(target.Interfaces);
                    }
                    else if (target.IsInterface) interfaces.Add(IndexOf(edge.Type));
                    else if (target.IsClass) AddBase(IndexOf(edge.Type));
                    else retained.Add(edge);
                }
                normalized[index] = proof with { BaseIndex = basis, Interfaces = interfaces.Distinct().ToArray(), ReferenceKnown = proof.ReferenceKnown || basis >= 0,
                    ParameterBounds = retained.Count == 0 ? null : new(retained, basis, interfaces.Distinct().ToArray()) };
                visiting.Remove(index);
            }
            return normalized;
        }
    }
}
