using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static bool HashingEnabled=>(activeFeatures&ScalarFeatures.RecordHashing)!=0;
    private static bool RecordHashSignature(ScalarStructMethod method)=>method.Name=="GetHashCode"&&method.Declaration.Parameters.Count==0&&ExtendedBinder.GenericOf(method.Declaration) is null;
    private static bool NativeTypeObjectMethod(ScalarStructMethod method)=>(activeFeatures&ScalarFeatures.TypeIdentity)!=0&&method.Owner>=0&&activeStructs![method.Owner].IsNativeType;
    private static bool NativeObjectOverrideSignature(ScalarStructMethod method)=>(DisplayEnabled||NativeTypeObjectMethod(method))&&RecordTextSignature(method)&&method.Declaration.ReturnType=="string"||(HashingEnabled||NativeTypeObjectMethod(method))&&RecordHashSignature(method)&&method.Declaration.ReturnType=="int"||(activeFeatures&ScalarFeatures.Boxing)!=0&&ObjectEqualsSignature(method);
    private static ScalarStructMethod? FindRecordHash(int index)
    {
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)if(activeStructs![k].Methods.FirstOrDefault(m=>RecordHashSignature(m)&&(activeStructs[k].IsRecord||m.IsOverride)) is {} method)return method;
        return null;
    }
    private static void EnsureRecordHashApis()
    {
        if(!HashingEnabled)return;
        int Depth(ScalarStruct owner){var depth=0;for(var k=owner.BaseIndex;k>=0;k=activeStructs![k].BaseIndex)depth++;return depth;}
        foreach(var owner in activeStructs!.Where(s=>s.IsRecord&&!s.IsTypeParameter&&(s.Template<0||s.Template==IndexOf(s.Id))).OrderBy(Depth).ToArray())
        {
            var index=IndexOf(owner.Id);var methods=owner.Methods.ToList();
            var conflict=owner.Fields.FirstOrDefault(f=>f.Owner==index&&!f.Hidden&&f.Name=="GetHashCode")??owner.StaticFields.FirstOrDefault(f=>!f.Hidden&&f.Name=="GetHashCode");var property=owner.Properties.FirstOrDefault(p=>p.Owner==index&&p.Name=="GetHashCode");
            if(conflict is not null||property is not null)throw DeclarationError("AI3106","A field or property cannot replace the record GetHashCode method.",conflict?.Location??property!.Location);
            var signatures=new HashSet<string>(StringComparer.Ordinal);
            foreach(var method in methods.Where(m=>m.Name=="GetHashCode"))
                if(!signatures.Add(DeconstructSignature(owner,method)))throw DeclarationError("AI3106","Duplicate GetHashCode parameter signature.",method.Declaration.Location);
            if(methods.FirstOrDefault(RecordHashSignature) is {} declared)
            {
                if(!declared.IsPublic||declared.IsInternal||declared.IsStatic||!declared.IsOverride||declared.IsVirtual||declared.Declaration.ReturnType!="int"||declared.IsSealed&&(!owner.IsClass||!owner.IsSealed)||declared.IsAbstract&&!owner.IsAbstract)
                    throw DeclarationError("AI3106","Record GetHashCode requires a public instance Int32 override; an unsealed record must allow overriding.",declared.Declaration.Location);
            }
            else
            {
                if(owner.IsClass&&owner.BaseIndex>=0&&FindRecordHash(owner.BaseIndex) is {IsAbstract:true})
                    throw DeclarationError("AI3106","A generated hash cannot call an abstract base GetHashCode implementation.",owner.Location);
                var at=owner.Location;var declaration=new FunctionDeclaration("GetHashCode",[],"int",[],[new(ContractKind.Ensures,"true",at)],new FunctionBody([]){ScalarSource=new("",at)},[],at);
                methods.Add(new("GetHashCode",owner.Name+".$record_hash",true,false,declaration){Owner=index,IsRecordHashApi=true,IsOverride=true,IsReadonly=!owner.IsClass,ReceiverMode=owner.IsClass?ScalarParameterMode.Value:ScalarParameterMode.In});
            }
            activeStructs![index]=owner with{Methods=methods};
        }
    }
    private sealed partial class ExtendedBinder
    {
        private readonly bool _recordHashApi;
        private ScalarExpression HashCombine(ScalarExpression current,ScalarExpression item,SourceLocation at)
            =>new("+",ScalarType.Int,at,Left:new("*",ScalarType.Int,at,Left:current,Right:new("literal",ScalarType.Int,at,"-1521134295")){Wraps=true},Right:item){Wraps=true};
        private ScalarExpression HashRecordValue(ScalarExpression value,SourceLocation at)
        {
            if(IsEnum(value.Type))value=new("cast",EnumOf(value.Type).Underlying,at,Left:value);
            if(ScalarConstants.IsInteger(value.Type)||ScalarConstants.IsFloat(value.Type)||value.Type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal or ScalarType.String)
                return new("hash-scalar",ScalarType.Int,at,Left:value);
            if(IsClass(value.Type)&&StructOf(value.Type).IsRecord)
            {
                var local=HiddenLocal(value.Type);var item=new ScalarExpression("local",value.Type,at,local.EmittedName);
                var isNull=new ScalarExpression("==",ScalarType.Bool,at,Left:item,Right:new("null",value.Type,at));
                var call=MethodCall(item,new("GetHashCode",at),given:[]);
                return new("switch",ScalarType.Int,at,local.EmittedName,Left:value){Arguments=[isNull,new("literal",ScalarType.Int,at,"0"),new("literal",ScalarType.Bool,at,"true"),call]};
            }
            if(IsObject(value.Type)||IsClass(value.Type))return HashObject(value,at);
            if(IsStruct(value.Type))
            {
                var owner=StructOf(value.Type);
                if(owner.IsTypeParameter&&_checkOnly)return new("literal",ScalarType.Int,at,"0");
                if(FindRecordHash(IndexOf(owner.Id)) is {} method)
                {
                    var receiver=method.ReceiverMode==ScalarParameterMode.Value?value:new ScalarExpression("argument-receiver-copy",owner.Id,at,"temporary",Left:value);
                    return new("call",ScalarType.Int,at,method.QualifiedName){Arguments=[receiver]};
                }
                ScalarExpression result=new("literal",ScalarType.Int,at,"0");
                for(var i=0;i<owner.Fields.Count;i++)result=HashCombine(result,HashRecordValue(new("field",owner.Fields[i].Type,at,i.ToString(CultureInfo.InvariantCulture),Left:value),at),at);
                return result;
            }
            Fail("AI3100","This native value has no hash implementation.",at);return null!;
        }
        private List<ScalarStatement> BindRecordHashApi()
        {
            var owner=_struct!;var at=owner.Location;var self=new ScalarExpression("local",owner.Id,at,_this!.EmittedName);ScalarExpression result;
            if(owner.IsClass&&owner.BaseIndex>=0)
            {
                var method=FindRecordHash(owner.BaseIndex)!;
                if(method.IsAbstract)Fail("AI3106","A generated hash cannot call an abstract base GetHashCode implementation.",at);
                result=new("call",ScalarType.Int,at,method.QualifiedName){Arguments=[Upcast(self,_structs[method.Owner].Id)]};
            }
            else result=owner.IsClass?RecordContractValue(owner,self,at) is {} contract?HashRecordValue(contract,at):new("hash-contract",ScalarType.Int,at,Left:self):new("literal",ScalarType.Int,at,"0");
            for(var i=0;i<owner.Fields.Count;i++)
            {
                var field=owner.Fields[i];if(owner.IsClass&&field.Owner!=IndexOf(owner.Id))continue;
                result=HashCombine(result,HashRecordValue(new("field",field.Type,field.Location,i.ToString(CultureInfo.InvariantCulture),Left:self),field.Location),field.Location);
            }
            return [new("return",at,result)];
        }
    }
}
