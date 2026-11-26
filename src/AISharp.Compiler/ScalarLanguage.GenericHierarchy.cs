namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<int, IReadOnlyList<(string Name, SourceLocation Location)>>? genericBases;
    [ThreadStatic] private static bool hierarchyBusy;
    [ThreadStatic] private static bool slotsBusy;
    [ThreadStatic] private static int genericBindingDepth;
    [ThreadStatic] private static HashSet<string>? validatingTypeUses;
    [ThreadStatic] private static int validatedTypeUseCount;
    [ThreadStatic] private static bool pendingConstraintValidation;

    private static IEnumerable<string> SplitTypeList(string text)
    {
        var depth = 0; var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
            else if (text[i] is ',' or ';' && depth == 0) { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }

    private static string SubstituteMemberText(string text,IReadOnlyDictionary<string,ScalarType> map)
    {
        var dot=text.LastIndexOf('.');
        return dot<0?text:SubstituteTypeText(text[..dot],map)+text[dot..];
    }
    private static string SubstituteTypeText(string text, IReadOnlyDictionary<string, ScalarType> map) =>
        RewriteTypeIdentifiers(text, name => map.TryGetValue(name, out var type) ? ExtendedBinder.HierarchyTypeName(type) : null);
    private static string SubstituteSignatureTypeText(string text, IReadOnlyDictionary<string, ScalarType> map) =>
        RewriteTypeIdentifiers(text, name => map.TryGetValue(name, out var type) ? ExtendedBinder.SignatureTypeName(type) : null);

    private static void ResolveHierarchy(List<IReadOnlyList<(string Name, SourceLocation Location)>> bases)
    {
        for (var i = 0; i < bases.Count; i++) if (bases[i].Count > 0 || !genericBases!.ContainsKey(i)) genericBases![i] = bases[i];
        if (hierarchyBusy) return;
        hierarchyBusy = true;
        try { ValidateGenericBaseCycles(); ResolveHierarchyCore(); ValidateGenericInterfaceUniqueness(); }
        finally { hierarchyBusy = false; }
    }

    private static void ValidateGenericBaseCycles()
    {
        if (!genericBases!.Values.Any(list => list.Any(b => b.Name.Contains('<')))) return;
        var all = activeStructs!; var state = new Dictionary<int, int>();
        void Visit(int index)
        {
            if (state.GetValueOrDefault(index) == 2) return;
            if (state.GetValueOrDefault(index) == 1) throw DeclarationError("AI3106", $"Circular generic base dependency involving '{all[index].Name}'.", all[index].Location);
            state[index] = 1;
            if (genericBases!.TryGetValue(index, out var list)) foreach (var item in list)
            {
                var head = item.Name.Split('<')[0].Trim();
                var next = all.FindIndex(t => t.Name == IdentifierName(head) && !t.IsTypeParameter);
                if (next >= 0) Visit(next);
            }
            state[index] = 2;
        }
        for (var i = 0; i < all.Count; i++) if (all[i].Template < 0 || all[i].Template == i) Visit(i);
    }

    private static void ResolveSlots()
    {
        if (slotsBusy) return;
        slotsBusy = true;
        try { ResolveSlotsCore(); }
        finally { slotsBusy = false; }
    }

    private static void ValidateGenericInterfaceUniqueness()
    {
        var all = activeStructs!;
        for (var owner = 0; owner < all.Count; owner++)
        {
            var info = all[owner]; if (info.Template != owner || info.TypeParameters.Count == 0) continue;
            var variables = info.TypeArgumentList.ToHashSet(); var interfaces = InterfaceClosure(all, info.Interfaces).ToArray();
            bool CanUnify(ScalarType first, ScalarType second, Dictionary<ScalarType, ScalarType> substitutions)
            {
                ScalarType Follow(ScalarType type) { while (substitutions.TryGetValue(type, out var next)) type = next; return type; }
                bool Occurs(ScalarType variable, ScalarType type)
                {
                    type = Follow(type); if (type == variable) return true;
                    return (int)type >= StructTypeBase && (int)type < StructTypeBase + all.Count && all[IndexOf(type)].TypeArgumentList.Any(a => Occurs(variable, a));
                }
                first = Follow(first); second = Follow(second); if (first == second) return true;
                if (variables.Contains(first)) { if (Occurs(first, second)) return false; substitutions[first] = second; return true; }
                if (variables.Contains(second)) { if (Occurs(second, first)) return false; substitutions[second] = first; return true; }
                if ((int)first < StructTypeBase || (int)second < StructTypeBase || IndexOf(first) >= all.Count || IndexOf(second) >= all.Count) return false;
                var a = all[IndexOf(first)]; var b = all[IndexOf(second)];
                return a.Template >= 0 && a.Template == b.Template && a.TypeArgumentList.Zip(b.TypeArgumentList).All(pair => CanUnify(pair.First, pair.Second, substitutions));
            }
            for (var i = 0; i < interfaces.Length; i++) for (var j = i + 1; j < interfaces.Length; j++)
                if (CanUnify(all[interfaces[i]].Id, all[interfaces[j]].Id, []))
                    throw DeclarationError("AI3106", $"'{info.Name}' cannot implement interfaces that may unify for some type arguments.", info.Location);
        }
    }

    private static void RefreshGenericHierarchy()
    {
        if (!templateMembersReady || hierarchyBusy || slotsBusy || genericBindingDepth != 0) return;
        ResolveHierarchy([]); ResolveSlots();
        ValidatePendingTypeUses();
    }

    private static void ValidatePendingTypeUses()
    {
        if (!typeConstraintsReady || pendingConstraintValidation) return;
        pendingConstraintValidation = true;
        try
        {
            while (validatedTypeUseCount < typeUses!.Count)
            {
                var use = typeUses[validatedTypeUseCount++];
                new ExtendedBinder(activeFeatures, "", use.At).ValidateTypeUse(use);
            }
        }
        finally { pendingConstraintValidation = false; }
    }

    private sealed partial class ExtendedBinder
    {
        public ScalarType HierarchyType(string text, SourceLocation at) => Type(text, at);
        public static string HierarchyTypeName(ScalarType type)
        {
            var binder = new ExtendedBinder(activeFeatures, "", new(1, 1, 0));
            if (IsEnum(type)) return TypeIdentifierSyntax(binder.EnumOf(type).Name);
            if (IsStruct(type))
            {
                var info = binder.StructOf(type);
                if (info.ArrayElementType is { } element) return HierarchyTypeName(element) + "[]";
                if (info.IsStringArray || info.Name == StringArrayName) return "string[]";
                if (info.Template >= 0) return TypeIdentifierSyntax(activeStructs![info.Template].Name) + "<" + string.Join(", ", info.TypeArgumentList.Select(HierarchyTypeName)) + ">";
                return TypeIdentifierSyntax(info.Name);
            }
            return IsObject(type) ? "object" : ScalarConstants.Spelling(type);
        }
        public static string SignatureTypeName(ScalarType type)
        {
            if (IsStruct(type))
            {
                var info = activeStructs![IndexOf(type)];
                if (info.ArrayElementType is { } element) return SignatureTypeName(element) + "[]";
                if (info.IsTypeParameter) return "$" + ((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (info.Template >= 0 && !info.IsStringArray)
                    return TypeIdentifierSyntax(activeStructs[info.Template].Name) + "<" + string.Join(", ", info.TypeArgumentList.Select(SignatureTypeName)) + ">";
            }
            return HierarchyTypeName(type);
        }
        private Lexeme HeaderTypeToken()
        {
            var first = TypeHeadToken(); if (!IsSourceIdentifier(first.Text) && QualifiedBuiltinAlias(first.Text) is null) Fail("AI3106", $"Invalid type name '{first.Text}'.", first.Location);
            var text = new System.Text.StringBuilder(first.Text); var depth = 0;
            if (Current.Text == "<") do
            {
                var token = Next();
                if (token.Text == "") Fail("AI3106", "Unterminated type argument list.", first.Location);
                if (token.Text.All(c => c == '>')) depth -= token.Text.Length;
                else if (token.Text == "<") depth++;
                else if (token.Text is not ("," or "." or ":" or "::") && !(Has(ScalarFeatures.ProcessArguments) && token.Text is "[" or "]") && !IsSourceIdentifier(token.Text)) Fail("AI3106", $"Unexpected '{token.Text}' in a type argument list.", token.Location);
                if (depth < 0) Fail("AI3106", "Unexpected closing type argument bracket.", token.Location);
                text.Append(token.Text == "," ? ", " : token.Text);
            } while (depth > 0);
            while (Has(ScalarFeatures.TypedArrays) && Current.Text == "[" && TokenAt(_position + 1).Text == "]")
            { Next(); Next(); text.Append("[]"); }
            return new(text.ToString(), first.Location);
        }

        private bool QualifiedInterfaceMemberAt()
        {
            var next = QualifiedBuiltinAt(_position)?.End ?? _position + 1;
            if (TokenAt(next).Text == "<")
            {
                var depth = 0;
                do { var text = TokenAt(next++).Text; if (text == "<") depth++; else if (text.All(c => c == '>') && text.Length > 0) depth -= text.Length; else if (text is "" or "\n" or "{") return false; } while (depth > 0);
            }
            return TokenAt(next).Text == ".";
        }

        private Dictionary<ScalarType, ScalarType> ScopeTypeArguments(IReadOnlyDictionary<string, ScalarType> map, ScalarStruct? owner, GenericInfo? generic)
        {
            var identities = new Dictionary<ScalarType, ScalarType>();
            if (owner is { Template: >= 0, TypeArguments: not null })
                foreach (var original in _structs[owner.Template].TypeArguments!)
                    identities[original.Value] = owner.TypeArguments[original.Key];
            if (generic is not null)
                foreach (var parameter in generic.Parameters)
                    if (map.TryGetValue(parameter.Name, out var argument)) identities[(ScalarType)(StructTypeBase + parameter.Placeholder)] = argument;
            return identities;
        }

        private ScalarType ReboundType(ScalarType type, IReadOnlyDictionary<string, ScalarType> map, SourceLocation at, IReadOnlyDictionary<ScalarType, ScalarType>? identities = null)
        {
            if (!IsStruct(type)) return type;
            var info = StructOf(type);
            if (info.IsTypeParameter)
            {
                if (identities is not null && identities.TryGetValue(type, out var scoped)) return scoped;
                return map.TryGetValue(info.Name, out var argument) ? argument : type;
            }
            if(info.ArrayElementType is {} arrayElement)
            {
                var rebound=ReboundType(arrayElement,map,at,identities);
                return rebound==arrayElement?type:TypedArray(rebound,at);
            }
            if(info.NativeCollectionElement is {} collectionElement&&info.NativeCollectionKind is {} kind)
            {
                var rebound=ReboundType(collectionElement,map,at,identities);
                return rebound==collectionElement?type:ArrayCollectionTypeText(kind+"<"+DescriptorArgumentSyntax(rebound)+">",at,forceBuiltin:true)!.Value;
            }
            if(info.IsCheckInstance&&IsIteratorInterface(info.Name)&&IteratorShape(type) is {} iterator)
            {
                var rebound=ReboundType(iterator.Element,map,at,identities);
                return rebound==iterator.Element?type:IteratorTypeText((iterator.Sequence?"IEnumerable":"IEnumerator")+"<"+DescriptorArgumentSyntax(rebound)+">",at,forceBuiltin:true)!.Value;
            }
            if (info.Template < 0) return type;
            var arguments = info.TypeArgumentList.Select(t => ReboundType(t, map, at, identities)).ToArray();
            return arguments.SequenceEqual(info.TypeArgumentList) ? type : InstanceOf(info.Template, arguments, at);
        }

        private ScalarStruct ReboundConstraint(ScalarStruct parameter, IReadOnlyDictionary<string, ScalarType> map, SourceLocation at, IReadOnlyDictionary<ScalarType, ScalarType>? identities = null) => parameter with
        {
            BaseIndex = parameter.BaseIndex < 0 ? -1 : IndexOf(ReboundType(_structs[parameter.BaseIndex].Id, map, at, identities)),
            Interfaces = parameter.Interfaces.Select(i => IndexOf(ReboundType(_structs[i].Id, map, at, identities))).ToList(),
            ParameterBounds = parameter.ParameterBounds is not { } bounds ? null : bounds with
            {
                Dependencies = bounds.Dependencies.Select(d => d with { Type = ReboundType(d.Type, map, at, identities) }).ToList(),
                DeclaredBaseIndex = bounds.DeclaredBaseIndex < 0 ? -1 : IndexOf(ReboundType(_structs[bounds.DeclaredBaseIndex].Id, map, at, identities)),
                DeclaredInterfaces = bounds.DeclaredInterfaces.Select(i => IndexOf(ReboundType(_structs[i].Id, map, at, identities))).ToList()
            }
        };

        private ScalarStructMethod ReboundInterfaceRoot(ScalarStructMethod root)
        {
            if (_typeArgs is null) return root;
            var identities = ScopeTypeArguments(_typeArgs, _struct, GenericOf(_function));
            var original = _structs[root.Owner]; var type = ReboundType(original.Id, _typeArgs, root.Declaration.Location, identities);
            if (type == original.Id) return root;
            return StructOf(type).Methods.Single(m => m.Name == root.Name && m.InterfaceQualifier is null);
        }
    }
}
