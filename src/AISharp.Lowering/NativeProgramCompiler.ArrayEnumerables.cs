using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static void ValidateArrayEnumerableLayouts()
    {
        bool PlainAbstract(ScalarStruct owner) => owner.IsClass && owner.IsAbstract && !owner.IsInterface && !owner.IsSealed && !owner.IsStatic
            && !owner.IsReadonly && !owner.IsRecord && !owner.IsCheckInstance && !owner.IsStringArray && owner.ArrayElementType is null && owner.NativeCollectionKind is null && owner.NativeCollectionElement is null
            && owner.BoxedType is null && !owner.IsObjectRoot && !owner.IsStringObject && !owner.IsDelegate && !owner.IsDelegateFunction
            && !owner.IsNativeType && !owner.IsNativeRuntimeType && owner.BaseIndex < 0 && owner.Interfaces.Count == 0
            && owner.Fields.Count == 0 && owner.StaticFields.Count == 0 && owner.Properties.Count == 0
            && owner.Template < 0 && owner.TypeParameters.Count == 0 && owner.TypeArgumentList.Count == 0 && owner.TypeArguments is null;
        bool Method(ScalarStruct owner, ScalarStructMethod method) => method.IsPublic && !method.IsStatic && !method.IsConstructor
            && !method.IsAccessor && method.IsAbstract && !method.IsVirtual && !method.IsOverride && !method.IsSealed
            && method.Owner == (int)owner.Id - ScalarLanguage.StructTypeBase && method.Slot == method.QualifiedName
            && method.QualifiedName == owner.Name + "." + method.Name && method.Declaration.Parameters.Count == 0;
        bool ElementName(string text, ScalarType element)
        {
            var known = text switch {
                "bool" => ScalarType.Bool, "char" => ScalarType.Char, "string" => ScalarType.String, "decimal" => ScalarType.Decimal,
                "f32" => ScalarType.F32, "float" or "double" or "f64" => ScalarType.F64, "object" => (ScalarType)ScalarLanguage.ObjectTypeId,
                _ => ScalarConstants.FromSpelling(text)
            };
            if(known==element&&(!ScalarConstants.IsNative(element)||!(layouts??[]).Any(t=>t.Name==text&&!t.IsTypeParameter&&t.TypeParameters.Count==0))||IsStructType(element)&&Layout(element).Name==text||(typeEnums??[]).Any(e=>e.Id==element&&e.Name==text))return true;
            try{return text==ScalarLanguage.SequenceElementSyntax(layouts??[],typeEnums??[],element);}catch(InvalidOperationException){return false;}
        }
        foreach (var owner in layouts ?? [])
        {
            if (owner.NativeEnumerableElement is not { } element) continue;
            var index = (int)owner.Id - ScalarLanguage.StructTypeBase;
            var suffix = owner.Name.StartsWith("__IEnumerable_", StringComparison.Ordinal) ? owner.Name[14..] : "";
            var enumerator = (layouts ?? []).FirstOrDefault(t => t.Name == "__IEnumerator_" + suffix);
            if (index < 0 || index >= layouts!.Count || !ReferenceEquals(layouts[index], owner) || suffix.Length == 0
                || !PlainAbstract(owner) || owner.Methods is not [{ Name: "GetEnumerator" } get] || !Method(owner, get)
                || get.Declaration.ReturnType != "__IEnumerator_" + suffix || enumerator is null || !PlainAbstract(enumerator)
                || enumerator.NativeEnumerableElement is not null || enumerator.Methods.Count != 3 || !enumerator.Methods.All(m => Method(enumerator, m))
                || !enumerator.Methods.Any(m => m.Name == "MoveNext" && m.Declaration.ReturnType == "bool")
                || !enumerator.Methods.Any(m => m.Name == "Dispose" && m.Declaration.ReturnType == "void")
                || !enumerator.Methods.Any(m => m.Name == "get_Current" && ElementName(m.Declaration.ReturnType, element)))
                throw new Rejected(owner.Location, "An array enumerable requires an exact compiler-owned abstract descriptor and matching element identity.");
        }
    }
}
