using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static void ValidateTypedArrayLayouts()
    {
        var arrays = (layouts ?? []).Where(t => t.ArrayElementType is not null).ToArray();
        if (arrays.Length > 128) throw new Rejected(new(1,1,0), "More than 128 native typed-array layouts are unsupported.");
        var elements = new HashSet<ScalarType>();
        foreach (var owner in layouts ?? [])
        {
            if (owner.ArrayElementType is not { } element)
            {
                if (!owner.IsCheckInstance && owner.Name.StartsWith(ScalarLanguage.NativeArrayPrefix, StringComparison.Ordinal)) throw new Rejected(owner.Location, "Reserved array storage requires its element metadata.");
                continue;
            }
            var index = (int)owner.Id - ScalarLanguage.StructTypeBase;
            if (index < 0 || index >= layouts!.Count || !ReferenceEquals(layouts[index], owner)
                || !elements.Add(element) || owner.Name != ScalarLanguage.NativeArrayPrefix + ((int)element).ToString(CultureInfo.InvariantCulture)
                || !owner.IsClass || !owner.IsSealed || owner.IsInterface || owner.IsAbstract || owner.IsStatic || owner.IsReadonly || owner.IsCheckInstance
                || owner.IsRecord || owner.IsStringArray || owner.IsStringObject || owner.IsObjectRoot || owner.BoxedType is not null || owner.NativeEnumerableElement is not null || owner.NativeCollectionKind is not null || owner.NativeCollectionElement is not null
                || owner.IsDelegate || owner.IsDelegateFunction || owner.IsNativeType || owner.IsNativeRuntimeType || owner.BaseIndex >= 0
                || owner.Template >= 0 || owner.TypeParameters.Count != 0 || owner.TypeArgumentList.Count != 0 || owner.TypeArguments is not null
                || owner.Interfaces.Count != 0 || owner.Methods.Count != 0 || owner.Properties.Count != 0 || owner.StaticFields.Count != 0
                || owner.RuntimeTypeDefinition is not null || owner.Fields is not [{Name:"Length",Type:ScalarType.Int,StorageType:ScalarType.Int,IsPublic:true,IsReadonly:true,Hidden:false} field]
                || field.Owner != index || field.IsProperty || field.IsEvent || field.IsInit || field.IsRequired || field.IsInternal || field.WritePrivate
                || element == ScalarType.Unit || !(BoxPayload(element) || IsObjectType(element) || element == ScalarType.String || IsClassType(element) || owner.IsCheckInstance && IsStructType(element)))
                throw new Rejected(owner.Location, "A native typed array requires an exact compiler-owned layout and storage element.");
            var path = new HashSet<ScalarType> { owner.Id }; var nested = element;
            while (IsStructType(nested) && Layout(nested).ArrayElementType is { } next)
            {
                if (!path.Add(nested) || path.Count > 32) throw new Rejected(owner.Location, "Native array element metadata must be acyclic and at most 32 levels deep.");
                nested = next;
            }
        }
    }
}
