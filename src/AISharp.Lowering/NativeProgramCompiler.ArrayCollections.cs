using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static void ValidateArrayCollectionLayouts()
    {
        string[] kinds=["ICollection","IList","IReadOnlyCollection","IReadOnlyList"];
        foreach(var owner in layouts??[])
        {
            var reserved=kinds.Any(k=>owner.Name.StartsWith(ScalarLanguage.NativeCollectionPrefix+k+"_",StringComparison.Ordinal));
            if(owner.NativeCollectionKind is null&&owner.NativeCollectionElement is null&&!reserved)continue;
            var kind=owner.NativeCollectionKind;var index=(int)owner.Id-ScalarLanguage.StructTypeBase;
            void Reject()=>throw new Rejected(owner.Location,"An array collection requires an exact compiler-owned interface descriptor, hierarchy and member signatures.");
            if(kind is null||!kinds.Contains(kind)||owner.NativeCollectionElement is not {} element
                ||index<0||index>=layouts!.Count||!ReferenceEquals(layouts[index],owner)
                ||!owner.Name.StartsWith(ScalarLanguage.NativeCollectionPrefix+kind+"_",StringComparison.Ordinal)
                ||!owner.IsClass||!owner.IsInterface||owner.IsAbstract||owner.IsSealed||owner.IsStatic||owner.IsReadonly||owner.IsRecord||owner.IsCheckInstance
                ||owner.IsStringArray||owner.ArrayElementType is not null||owner.NativeEnumerableElement is not null||owner.BoxedType is not null||owner.IsObjectRoot||owner.IsStringObject
                ||owner.IsDelegate||owner.IsDelegateFunction||owner.IsNativeType||owner.IsNativeRuntimeType||owner.BaseIndex>=0
                ||owner.Fields.Count!=0||owner.StaticFields.Count!=0||owner.Template>=0||owner.TypeParameters.Count!=0||owner.TypeArguments is not null||owner.TypeArgumentList.Count!=0)
            {Reject();continue;}
            var key=owner.Name[(ScalarLanguage.NativeCollectionPrefix+kind+"_").Length..];
            var enumerable=layouts!.FirstOrDefault(t=>t.Name=="__IEnumerable_"+key&&t.NativeEnumerableElement==element);
            var enumerator=layouts.FirstOrDefault(t=>t.Name=="__IEnumerator_"+key);
            if(key.Length==0||enumerable is null||enumerator is null){Reject();continue;}
            var syntax=enumerator.Methods.Single(m=>m.Name=="get_Current").Declaration.ReturnType;
            var parent=kind=="IList"?"ICollection":kind=="IReadOnlyList"?"IReadOnlyCollection":null;
            if(parent is null?owner.Interfaces.Count!=0:owner.Interfaces.Count!=1||owner.Interfaces[0]<0||owner.Interfaces[0]>=layouts.Count
                ||layouts[owner.Interfaces[0]].Name!=ScalarLanguage.NativeCollectionPrefix+parent+"_"+key||layouts[owner.Interfaces[0]].NativeCollectionElement!=element)
            {Reject();continue;}
            var spec=new List<(string Name,string Result,string[] Parameters,string? Accessor)>();
            void M(string name,string result,string[] parameters,string? accessor=null)=>spec.Add((name,result,parameters,accessor));
            if(kind is "ICollection" or "IReadOnlyCollection")
            {M("get_Count","int",[],"get:Count");M("GetEnumerator","__IEnumerator_"+key,[]);}
            if(kind=="ICollection")
            {M("get_IsReadOnly","bool",[],"get:IsReadOnly");M("Add","void",[syntax]);M("Clear","void",[]);M("Remove","bool",[syntax]);M("Contains","bool",[syntax]);M("CopyTo","void",[syntax+"[]","int"]);}
            if(kind is "IList" or "IReadOnlyList")M("get_this__indexer_0",syntax,["int"],"get:this");
            if(kind=="IList")
            {M("set_this__indexer_0","unit",["int",syntax],"set:this");M("Insert","void",["int",syntax]);M("RemoveAt","void",["int"]);M("IndexOf","int",[syntax]);}
            if(owner.Methods.Count!=spec.Count){Reject();continue;}
            foreach(var expected in spec)
            {
                var matches=owner.Methods.Where(m=>m.Name==expected.Name).ToArray();
                if(matches.Length!=1){Reject();continue;}var m=matches[0];
                if(!m.IsPublic||m.IsStatic||m.IsConstructor||!m.IsAbstract||m.IsVirtual||m.IsOverride||m.IsSealed||m.IsReadonly
                    ||m.Owner!=index||m.QualifiedName!=owner.Name+"."+m.Name||m.Slot!=m.QualifiedName||m.InterfaceQualifier is not null||m.InterfaceSlot is not null
                    ||m.IsAccessor!=(expected.Accessor is not null)||m.AccessorMember!=expected.Accessor||m.Declaration.ReturnType!=expected.Result
                    ||!m.Declaration.Parameters.Select(p=>p.Type).SequenceEqual(expected.Parameters)||m.Declaration.Parameters.Any(p=>p.Ownership!=default))Reject();
            }
            var propertyNames=kind=="ICollection"?new[]{"Count","IsReadOnly"}:kind=="IReadOnlyCollection"?new[]{"Count"}:new[]{"this"};
            if(owner.Properties.Count!=propertyNames.Length||!owner.Properties.Select(p=>p.Name).Order().SequenceEqual(propertyNames.Order())){Reject();continue;}
            foreach(var p in owner.Properties)
            {
                var item=p.Name=="this";var type=item?element:p.Name=="Count"?ScalarType.Int:ScalarType.Bool;
                var getter=owner.Name+".get_"+(item?"this__indexer_0":p.Name);var setter=item&&kind=="IList"?owner.Name+".set_this__indexer_0":null;
                if(p.Type!=type||p.Owner!=index||!p.IsPublic||p.IsAbstract||p.IsStatic||p.IsEvent||p.IsRequired||p.IsInit||p.IsAutomatic||p.Backing is not null
                    ||p.ReferenceMode!=ScalarParameterMode.Value||p.IsIndexer!=item||!p.HasGet||p.Getter!=getter||p.HasSet!=(setter is not null)||p.Setter!=setter
                    ||p.SetPrivate||p.GetPrivate||p.IsInternal||p.InterfaceQualifier is not null||!p.IndexParameters.Select(x=>x.Type).SequenceEqual(item?["int"]:[]))Reject();
            }
        }
    }
}
