namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private bool IsValueType(ScalarType type) => IsNum(type) || type is ScalarType.Bool or ScalarType.Char || IsEnum(type)
            || IsStruct(type) && (StructOf(type).IsTypeParameter ? StructOf(type).HasValueConstraint : !StructOf(type).IsClass);

        private bool IsUnmanagedType(ScalarType type, HashSet<ScalarType> visiting)
        {
            if (IsNum(type) || type is ScalarType.Bool or ScalarType.Char || IsEnum(type)) return true;
            if (!IsStruct(type)) return false;
            var info = StructOf(type);
            if (info.IsTypeParameter) return info.HasUnmanagedConstraint;
            if (info.IsClass || !visiting.Add(type)) return false;
            try { return info.Fields.All(f => IsUnmanagedType(f.Type, visiting)); }
            finally { visiting.Remove(type); }
        }

        private bool HasDefaultConstructor(ScalarType type, bool alreadyCheckedDeclaration)
        {
            if (IsObject(type)) return true;
            if (IsTypeParameterType(type)) return StructOf(type).HasValueConstraint || StructOf(type).HasConstructorConstraint;
            if (IsStruct(type) && !alreadyCheckedDeclaration && RequiredMemberNames(StructOf(type)).Any()) return false;
            if (IsValueType(type)) return true;
            if (!IsClass(type)) return false;
            var info = StructOf(type);
            if (info.IsAbstract || info.IsInterface || info.IsStatic || info.IsStringArray || info.ArrayElementType is not null || IsDelegate(type) || IsTaskType(type)
                || info.Name.StartsWith("__Grp_", StringComparison.Ordinal)) return false;
            if(RecordConstructorsEnabled(info))return info.Methods.Any(m=>m.IsConstructor&&m.IsPublic&&!m.IsInternalConstructor&&m.Declaration.Parameters.Count==0);
            var constructor = info.Methods.FirstOrDefault(m => m.IsConstructor);
            return constructor is null ? !info.IsRecord || info.Positional == 0 : constructor.IsPublic && !constructor.IsInternalConstructor && constructor.Declaration.Parameters.Count == 0;
        }

        // A value-type parameter uses its zero value unless it declares a parameterless constructor.
        // Generic construction wraps constructor faults, without reopening required-member checks.
        private ScalarExpression NewValueTypeParameter(ScalarType type, Lexeme token, SourceLocation at)
        {
            if (Take("(")) Expect(")");
            else if (Current.Text != "{") Fail("AI3106", "Expected '(' or '{' after the type parameter in 'new'.");
            ScalarExpression Initialized(ScalarExpression value)
            {
                if (Current.Text != "{") return value;
                var position = _position;
                Next(); NewLines();
                if (Take("}")) return value;
                _position = position;
                if (Has(ScalarFeatures.RequiredMembers) && IsStruct(type))
                    return RequiredObjectInitializer(StructOf(type), value, at, enforceRequired: false);
                Expect("{"); NewLines(); Expect("}"); return value;
            }
            if (IsObject(type))
            {
                if(Has(ScalarFeatures.Boxing))
                {
                    var index=_structs.Count;if(index>=1000)Fail("AI3100","Too many types for native object storage.",at);
                    var root=(ScalarType)(StructTypeBase+index);
                    _structs.Add(new(NativeObjectPrefix+index.ToString(System.Globalization.CultureInfo.InvariantCulture),root,[],[],at,IsClass:true){IsSealed=true,IsObjectRoot=true});
                    return Initialized(new("toobject",type,at,Left:new ScalarExpression("new",root,at)));
                }
                var name = "__object_value_" + _structs.Count;
                while (_structs.Any(t => t.Name == name)) name += "_";
                var id = (ScalarType)(StructTypeBase + _structs.Count);
                _structs.Add(new(name, id, [], [], at, IsClass: true) { IsSealed = true });
                return Initialized(new("toobject", type, at, Left: new ScalarExpression("new", id, at)));
            }
            if (IsStruct(type) && StructOf(type) is var info
                && (info.Methods.Any(m => m.IsConstructor && m.Declaration.Parameters.Count == 0)
                    || info.IsRecord && info.InstanceInitializers.Count > 0 && info.HasRecordPrimaryConstructor && info.Positional == 0))
            {
                var recordConstructor=RecordConstructorsEnabled(info)?info.Methods.First(m=>m.IsConstructor&&m.Declaration.Parameters.Count==0):null;
                if(recordConstructor?.RecordConstructorKind=="zero")return Initialized(DefaultValue(type,at));
                var constructorName=recordConstructor?.QualifiedName??(info.IsRecord&&info.InstanceInitializers.Count>0?RecordFactoryName(info):info.Methods.First(m=>m.IsConstructor&&m.Declaration.Parameters.Count==0).QualifiedName);
                var name = info.Name + ".<generic-value-new>";
                if (requestedInstances!.Add(name))
                {
                    var construct = new ScalarExpression("call", type, at, constructorName) { Arguments = [] };
                    var body = new ScalarStatement("try", at,
                        Body: [new ScalarStatement("return", at, construct)],
                        Alternative: [new ScalarStatement("catch", at, Name: "*", Body:
                            [new ScalarStatement("throw", at, Name: "System.Reflection.TargetInvocationException")])]);
                    Lift(new ScalarFunction(name, [], type, [body], [TrueContract(at)], at));
                }
                return Initialized(new("call", type, at, name) { Arguments = [] });
            }
            return Initialized(DefaultValue(type, at));
        }
    }
}
