using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static string? activeDisplayModule;
    private const string ObjectDisplayTarget="StringBuilder.$object_display";
    private static string RuntimeDisplayType(ScalarType type)
    {
        var scalar=type switch
        {
            ScalarType.Int=>"Int32",ScalarType.Long=>"Int64",ScalarType.UInt=>"UInt32",ScalarType.ULong=>"UInt64",ScalarType.Short=>"Int16",ScalarType.UShort=>"UInt16",ScalarType.Byte=>"Byte",ScalarType.SByte=>"SByte",ScalarType.Bool=>"Boolean",ScalarType.Char=>"Char",ScalarType.String=>"String",ScalarType.Decimal=>"Decimal",ScalarType.F32=>"Single",ScalarType.F64=>"Double",ScalarType.NInt=>"IntPtr",ScalarType.NUInt=>"UIntPtr",_=>null
        };
        if(scalar is not null)return "System."+scalar;
        if((int)type==ObjectTypeId)return "System.Object";
        var prefix=string.IsNullOrEmpty(activeDisplayModule)?"":activeDisplayModule+".";
        if((int)type>=EnumTypeBase&&(int)type<StructTypeBase)return activeEnums![(int)type-EnumTypeBase] is { SourceName: {} en } enumeration?(enumeration.SourceFullName??enumeration.SourceNamespace+"."+en):prefix+activeEnums![(int)type-EnumTypeBase].Name;
        var owner=activeStructs![IndexOf(type)];if(owner.IsObjectRoot)return "System.Object";if(owner.IsStringArray)return "System.String[]";
        if(owner.Name==DisplayBuilderName&&owner.Fields.Any(f=>f.Name==DisplayBuilderField))return "System.Text.StringBuilder";
        var name=owner.SourceName is {} sourceName?(owner.SourceFullName??owner.SourceNamespace+"."+sourceName):prefix+owner.Name.Split('<')[0];return owner.TypeArgumentList.Count==0?name:name+((owner.SourceOwnArity??owner.TypeArgumentList.Count) is var own&&own>0?"`"+own.ToString(CultureInfo.InvariantCulture):"")+"["+string.Join(",",owner.TypeArgumentList.Select(RuntimeDisplayType))+"]";
    }
    private static bool EmitObjectDisplayApi(List<ScalarFunction> members,ScalarFeatures features,IReadOnlyList<FunctionDeclaration> functions,IReadOnlyList<ScalarFunction> topLevel)
    {
        if((features&ScalarFeatures.RecordDisplay)==0)return false;var found=false;
        void Value(ScalarExpression? value){if(value is null)return;if(value.Operation=="call"&&value.Value==ObjectDisplayTarget)found=true;Value(value.Left);Value(value.Right);if(value.Arguments is not null)foreach(var arg in value.Arguments)Value(arg);}
        void Statements(IReadOnlyList<ScalarStatement> statements){foreach(var statement in statements){Value(statement.Expression);Value(statement.Increment);if(statement.Initializer is not null)Statements([statement.Initializer]);if(statement.Body is not null)Statements(statement.Body);if(statement.Alternative is not null)Statements(statement.Alternative);}}
        foreach(var function in topLevel.Concat(members).Concat(liftedFunctions!)){Statements(function.Body);foreach(var contract in function.Contracts)Value(contract.Expression);}
        if(!found||!requestedInstances!.Add("object-display:"+activeStructs!.Count.ToString(CultureInfo.InvariantCulture)))return false;
        var binder=new ExtendedBinder(new FunctionDeclaration("__object_display",[],"string",[],[],new FunctionBody([]){ScalarSource=new("",new(1,1,0))},[],new(1,1,0)),features,functions);
        var generated=binder.ObjectDisplayFunction();var previous=members.FindIndex(f=>f.Name==ObjectDisplayTarget);if(previous<0)members.Add(generated);else members[previous]=generated;
        return true;
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarExpression DisplayObject(ScalarExpression value,SourceLocation at)=>new("call",ScalarType.String,at,ObjectDisplayTarget){Arguments=[IsObject(value.Type)?value:new("toobject",(ScalarType)ObjectTypeId,at,Left:value)]};
        private bool ObjectTextImplementation(ScalarStructMethod method)
        {
            if(method.Slot is not {} slot)return method.IsOverride;
            var root=_structs.SelectMany(s=>s.Methods).FirstOrDefault(m=>m.QualifiedName==slot);
            return root is not null&&(root.IsOverride||_structs[root.Owner].IsRecord);
        }
        public ScalarFunction ObjectDisplayFunction()
        {
            var at=_function.Location;var type=(ScalarType)ObjectTypeId;var self=new ScalarExpression("local",type,at,"__object");var body=new List<ScalarStatement>();
            foreach(var owner in _structs.Where(s=>s.IsClass&&!s.IsInterface&&!s.IsAbstract&&!s.IsTypeParameter&&!s.IsCheckInstance).ToArray())
            {
                ScalarStructMethod? implementation=null;
                for(var k=IndexOf(owner.Id);k>=0&&implementation is null;k=_structs[k].BaseIndex)
                    implementation=_structs[k].Methods.FirstOrDefault(m=>RecordTextSignature(m)&&m.IsPublic&&!m.IsStatic&&(ObjectTextImplementation(m)||_structs[k].IsRecord||IsDisplayBuilder(_structs[k])));
                ScalarExpression value;
                if(owner.IsStringObject)value=StringObjectPayload(owner,self,at);
                else if(owner.BoxedType is {} payload)
                {
                    var stored=BoxPayload(owner,self,at);
                    var method=IsStruct(payload)?ObjectOverride(StructOf(payload),RecordTextSignature):null;
                    value=method is not null?InterfaceImplementationCall(method,stored,[],ScalarType.String,at):IsStruct(payload)?new("literal",ScalarType.String,at,RuntimeDisplayType(payload)):DisplayValue(stored,at);
                }
                else if(implementation is null)value=new("literal",ScalarType.String,at,RuntimeDisplayType(owner.Id));
                else
                {
                    var declaring=_structs[implementation.Owner];var receiver=new ScalarExpression("downcast",declaring.Id,at,Left:self);
                    value=new("call",ScalarType.String,at,implementation.QualifiedName){Arguments=[receiver]};
                }
                body.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:self),Body:[new("return",at,value)]));
            }
            body.Add(new("return",at,new("literal",ScalarType.String,at,"")));
            return Erase(new(ObjectDisplayTarget,[new("__object",type)],ScalarType.String,body,[TrueContract(at)],at));
        }
    }
}
