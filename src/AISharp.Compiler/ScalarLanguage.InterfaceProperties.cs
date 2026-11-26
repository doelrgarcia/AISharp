namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<int, ScalarProperty>? staticInterfaceProperties;

    private static void MaterializeAutoInterfaceAccessors()
    {
        var all = activeStructs!;
        for (var k = 0; k < all.Count; k++)
        {
            if (all[k].IsInterface || all[k].IsTypeParameter) continue;
            var roots = ImplementedInterfaces(k).SelectMany(i => all[i].Methods).Where(m => m.IsAccessor && m.IsPublic && !m.IsSealed
                && (!m.IsStatic || m.IsAbstract || m.IsVirtual)).ToArray();
            foreach (var root in roots)
            {
                if (root.AccessorMember is not { } key || !key.StartsWith("get:",StringComparison.Ordinal) && !key.StartsWith("set:",StringComparison.Ordinal)) continue;
                var get = key.StartsWith("get:",StringComparison.Ordinal); var name = key[4..];
                if (name == "this" || get && ParameterPassing(root.Declaration.ReturnType).Mode != ScalarParameterMode.Value) continue;
                var field = root.IsStatic ? FindStaticField(k,name) : all[k].Fields.FirstOrDefault(f => f.Name == name && !f.Hidden);
                if (field is not { IsProperty: true, IsPublic: true } || !get && (field.IsReadonly&&!field.IsInit || field.WritePrivate || field.IsInit != root.IsInitAccessor)) continue;
                var owner = field.Owner >= 0 ? field.Owner : k;
                var type = all[owner];
                if (type.Methods.Any(m => m.IsAccessor && m.AccessorMember == key && SameSignature(m.Declaration,root.Declaration))) continue;
                var element = ExtendedBinder.SignatureTypeName(field.Type);
                if (type.TypeArguments is { } typeArguments)
                    foreach (var argument in typeArguments)
                        if (all.Any(t => t.Id == argument.Value && t.IsTypeParameter))
                            element = System.Text.RegularExpressions.Regex.Replace(element,@"\$" + ((int)argument.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) + @"(?![0-9])",argument.Key);
                var functionName = (get ? "get_" : "set_") + name + "__interface";
                while (type.Methods.Any(m => m.Name == functionName)) functionName += "_";
                var function = new FunctionDeclaration(functionName,get ? [] : [new("value",element)],get ? element : "unit",[],[],
                    new FunctionBody([]) { ScalarSource = new(get ? "return field" : "field = value",field.Location) },[],field.Location);
                if (!SameSignature(function,root.Declaration)) continue;
                var adapter = new ScalarStructMethod(functionName,type.Name + "." + functionName,true,false,function)
                {
                    Owner = owner, IsAccessor = true, AccessorMember = key, Backing = field.Name, IsStatic = root.IsStatic, IsInitAccessor = !get && field.IsInit,
                    IsReadonly = !root.IsStatic && !type.IsClass && get,
                    ReceiverMode = !root.IsStatic && !type.IsClass && (activeFeatures & ScalarFeatures.MutableStructAccessors) != 0 ? get ? ScalarParameterMode.In : ScalarParameterMode.Ref : ScalarParameterMode.Value
                };
                all[owner] = type with { Methods = [.. type.Methods, adapter] };
            }
        }
    }

    private sealed partial class ExtendedBinder
    {
        private ScalarProperty? ConstrainedStaticProperty(ScalarExpression receiver, Lexeme member)
        {
            var info = StructOf(receiver.Type);
            ScalarProperty? property;
            if (info.IsTypeParameter)
            {
                var candidates = InterfaceClosure(_structs,info.Interfaces).SelectMany(i => _structs[i].Properties)
                    .Where(p => p.IsStatic && !p.IsIndexer && p.Name == member.Text).DistinctBy(p => (p.Owner,p.Name)).ToArray();
                candidates = candidates.Where(p => !candidates.Any(other => other.Owner != p.Owner && NominalDerives(_structs,other.Owner,p.Owner))).ToArray();
                if (candidates.Length == 0) return null;
                if (candidates.Length != 1) Fail("AI3106","The static property is ambiguous between interface constraints.",member.Location);
                property = candidates[0];
                staticInterfaceProperties![member.Location.Offset] = property;
            }
            else if (!staticInterfaceProperties!.TryGetValue(member.Location.Offset,out property)) return null;
            var originalOwner = _structs[property.Owner];
            var function = property.Getter ?? property.Setter;
            var method = originalOwner.Methods.First(m => m.QualifiedName == function);
            var rebound = ReboundInterfaceRoot(method);
            property = _structs[rebound.Owner].Properties.First(p => p.Name == property.Name && !p.IsIndexer);
            if (!property.IsStatic || !_structs[property.Owner].IsInterface || rebound.Slot is null)
                Fail("AI3106","A static property on a type parameter requires a static abstract or virtual interface contract.",member.Location);
            return property;
        }

        private ScalarExpression StaticInterfaceAccessorCall(ScalarExpression receiver, ScalarProperty property, ScalarStructMethod root, IReadOnlyList<ScalarExpression> arguments, ScalarType result, SourceLocation at)
        {
            var info = StructOf(receiver.Type);
            if (receiver.Value is null || _typeArgs?.ContainsKey(receiver.Value) != true)
                Fail("AI3106","A static abstract or virtual interface property can be accessed only on a constrained type parameter.",at);
            root = ReboundInterfaceRoot(root); var implementation = root;
            if (!info.IsTypeParameter)
            {
                if (!Derives(_structs,IndexOf(info.Id),root.Owner)) Fail("AI3107","The type argument does not satisfy its checked static property contract.",at);
                var resolution = ResolveInterfaceImplementation(IndexOf(info.Id),root);
                if (resolution.Ambiguous || resolution.Method is not { IsStatic: true, IsAbstract: false }) Fail("AI3107","The type argument has no unique static property implementation.",at);
                implementation = resolution.Method!;
            }
            return InterfaceImplementationCall(implementation,null,arguments.ToList(),result,at);
        }
    }
}
