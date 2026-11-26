using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static void ValidateBuiltinEquatableStorage(ScalarStruct storage, ScalarType payload)
    {
        var all=layouts??[];var index=(int)storage.Id-ScalarLanguage.StructTypeBase;
        var contracts=ScalarLanguage.BuiltinEquatableInterfaces(all,payload).ToArray();
        void Reject()=>throw new Rejected(storage.Location,"Builtin equality storage requires exact logical interface identities, signatures and checked adapters.");
        bool ParameterType(string text)=>text switch
        {
            "bool"=>payload==ScalarType.Bool,"char"=>payload==ScalarType.Char,"string"=>payload==ScalarType.String,"decimal"=>payload==ScalarType.Decimal,
            "f32"=>payload==ScalarType.F32,"f64" or "double" or "float"=>payload==ScalarType.F64,
            _=>ScalarConstants.FromSpelling(text)==payload,
        };
        if(!storage.Interfaces.SequenceEqual(contracts)||storage.Methods.Count!=contracts.Length)Reject();
        foreach(var contract in contracts)
        {
            var owner=all[contract];
            if(!owner.IsClass||!owner.IsInterface||owner.IsStatic||owner.IsSealed||owner.IsReadonly||owner.IsRecord||owner.IsAbstract||owner.IsTypeParameter||owner.IsCheckInstance
                ||owner.BoxedType is not null||owner.ArrayElementType is not null||owner.IsStringObject||owner.IsObjectRoot||owner.IsStringArray||owner.IsDelegate||owner.IsDelegateFunction
                ||owner.NativeEnumerableElement is not null||owner.NativeCollectionElement is not null||owner.NativeCollectionKind is not null||owner.IsNativeType||owner.IsNativeRuntimeType
                ||owner.Fields.Count!=0||owner.StaticFields.Count!=0||owner.Properties.Count!=0||owner.Interfaces.Count!=0||owner.BaseIndex>=0
                ||owner.StaticInitializers.Count!=0||owner.InstanceInitializers.Count!=0
                ||owner.Name!=ScalarLanguage.NativeEquatableName+"<"+payload+">"||owner.TypeParameters is not ["__native_equatable_item"]
                ||owner.TypeArguments is not {Count:1}||!owner.TypeArguments.TryGetValue("__native_equatable_item",out var mapped)||mapped!=payload
                ||owner.TypeVariances is not [0]||owner.TypeArgumentList.Count!=1||owner.TypeArgumentList[0]!=payload||owner.Methods is not [{Name:"Equals"}]){Reject();continue;}
            var root=owner.Methods[0];
            if(root.Owner!=contract||!root.IsAbstract||!root.IsPublic||root.IsInternal||root.IsStatic||root.IsConstructor||root.IsStaticConstructor||root.IsAccessor
                ||root.IsVirtual||root.IsOverride||root.IsSealed||root.IsReadonly||root.ReceiverMode!=ScalarParameterMode.Value||root.InterfaceQualifier is not null||root.InterfaceSlot is not null
                ||root.QualifiedName!=owner.Name+".Equals"||root.Slot!=root.QualifiedName||root.BoxedMethodTarget is not null
                ||root.Declaration.ReturnType!="bool"||root.Declaration.Parameters.Count!=1||!ParameterType(root.Declaration.Parameters[0].Type)
                ||root.Declaration.Effects.Count!=0||root.Declaration.Contracts.Count!=0)Reject();
            var matches=storage.Methods.Where(m=>m.QualifiedName==storage.Name+".$equatable_"+contract).ToArray();
            if(matches.Length!=1){Reject();continue;}
            var method=matches[0];
            if(method.Name!="Equals"||method.Owner!=index||!method.IsPublic||method.IsInternal||method.IsStatic||method.IsConstructor||method.IsStaticConstructor||method.IsReadonly
                ||method.ReceiverMode!=ScalarParameterMode.Value||method.IsVirtual||method.IsOverride||method.IsAbstract||method.IsSealed||method.IsAccessor||method.IsLifted
                ||method.Slot is not null||method.InterfaceQualifier is not null||method.InterfaceSlot is not null&&method.InterfaceSlot!=root.Slot
                ||method.RecordEqualityApi is not null||method.RecordDisplayApi is not null||method.IsRecordHashApi||method.IsRecordCustomEquality||method.IsRecordEqualityComparer
                ||method.BoxedMethodTarget!=ScalarLanguage.NativeBuiltinEquatableAdapter||method.Declaration!=root.Declaration)Reject();
        }
    }
}
