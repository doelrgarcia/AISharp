namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static void EnsureRecordTypeContracts()
    {
        var all=activeStructs;
        if((activeFeatures&ScalarFeatures.TypeIdentity)==0||all is null||all.FirstOrDefault(s=>s.IsNativeType) is not {} nativeType)return;
        int Depth(ScalarStruct owner){var depth=0;for(var k=owner.BaseIndex;k>=0;k=all[k].BaseIndex)depth++;return depth;}
        foreach(var original in all.Where(s=>s.IsRecord&&s.IsClass&&!s.IsTypeParameter&&(s.Template<0||s.Template==IndexOf(s.Id))).OrderBy(Depth).ToArray())
        {
            var index=IndexOf(original.Id);var owner=all[index];var methods=owner.Methods.ToList();var properties=owner.Properties.ToList();
            var field=owner.Fields.FirstOrDefault(f=>f.Owner==index&&!f.Hidden&&f.Name=="EqualityContract")??owner.StaticFields.FirstOrDefault(f=>!f.Hidden&&f.Name=="EqualityContract");
            if(field is not null)throw DeclarationError("AI3106","A field cannot replace the record EqualityContract property.",field.Location);
            if(methods.Any(m=>m.Name=="EqualityContract"&&!m.IsAccessor))throw DeclarationError("AI3106","A method cannot replace the record EqualityContract property.",methods.First(m=>m.Name=="EqualityContract"&&!m.IsAccessor).Declaration.Location);
            var property=properties.FirstOrDefault(p=>p.Owner==index&&p.Name=="EqualityContract");
            if(property is not null)
            {
                var getter=methods.FirstOrDefault(m=>m.QualifiedName==property.Getter);
                var expectedProtected=owner.BaseIndex>=0||!owner.IsSealed;
                var valid=property.Type==nativeType.Id&&!property.IsStatic&&!property.IsIndexer&&!property.IsEvent&&!property.IsPublic&&!property.IsInternal&&property.HasGet&&!property.GetPrivate&&property.ReferenceMode==ScalarParameterMode.Value&&property.InterfaceQualifier is null
                    &&property.IsProtectedMember==expectedProtected&&getter is not null&&!getter.IsStatic;
                valid&=owner.BaseIndex>=0?getter is {IsOverride:true}:owner.IsSealed?getter is {IsVirtual:false,IsOverride:false,IsAbstract:false,IsSealed:false}:getter is {IsVirtual:true} or {IsAbstract:true};
                if(!owner.IsSealed&&getter is {IsSealed:true})valid=false;
                if(!valid)throw DeclarationError("AI3106","Record EqualityContract requires the compiler-defined Type getter, access and overridable property signature.",property.Location);
            }
            else
            {
                var at=owner.Location;var target=owner.Name+".get_EqualityContract";var isProtected=owner.BaseIndex>=0||!owner.IsSealed;
                var declaration=new FunctionDeclaration("get_EqualityContract",[],"Type",[],[],new FunctionBody([]){ScalarSource=new("return typeof("+RecordSelfType(owner)+")",at)},[],at);
                methods.Add(new("get_EqualityContract",target,false,false,declaration){Owner=index,IsAccessor=true,AccessorMember="get:EqualityContract",IsProtectedMember=isProtected,IsVirtual=owner.BaseIndex<0&&!owner.IsSealed,IsOverride=owner.BaseIndex>=0});
                properties.Add(new("EqualityContract",nativeType.Id,"Type",false,true,target,false,null,false,null,at){Owner=index,IsProtectedMember=isProtected,IsOverride=owner.BaseIndex>=0,IsSynthesizedRecordTypeContract=true});
            }
            all[index]=owner with{Methods=methods,Properties=properties};
        }
    }
    private static void RefreshRecordTypeContractProperty(int index)
    {
        var all=activeStructs!;var instance=all[index];if(instance.Template<0||instance.Template==index)return;
        var property=all[instance.Template].Properties.FirstOrDefault(p=>p.IsSynthesizedRecordTypeContract);
        if(property is null||instance.Properties.Any(p=>p.Owner==index&&p.Name=="EqualityContract"))return;
        all[index]=instance with{Properties=[..instance.Properties,property with{Owner=index,Getter=instance.Name+".get_EqualityContract"}]};
    }
    private sealed partial class ExtendedBinder
    {
        private void CheckPropertyAccess(ScalarExpression receiver,ScalarStruct owner,ScalarProperty property,Lexeme member)
        {
            if(property.IsProtectedMember&&_struct is not null&&Derives(_structs,IndexOf(_struct.Id),IndexOf(owner.Id)))
            {
                if(!_baseAccessorReceivers.Contains(receiver)&&!Derives(_structs,IndexOf(receiver.Type),IndexOf(_struct.Id)))Fail("AI3119","A protected property requires this, base, or a receiver of the derived declaring type.",member.Location);
                return;
            }
            CheckAccess(owner,property.IsPublic,member.Text,member.Location);
        }
        private ScalarExpression? RecordContractValue(ScalarStruct owner,ScalarExpression self,SourceLocation at)
        {
            if(!Has(ScalarFeatures.TypeIdentity))return null;
            var property=FindProperty(IndexOf(owner.Id),"EqualityContract");
            return property is null?null:PropertyRead(self,property,new("EqualityContract",at));
        }
    }
}
