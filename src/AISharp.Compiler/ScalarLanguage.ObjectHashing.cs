using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private const string ObjectHashTarget="__native.$record_field_hash";
    private static bool EmitObjectHashApi(List<ScalarFunction> members,ScalarFeatures features,IReadOnlyList<FunctionDeclaration> functions,IReadOnlyList<ScalarFunction> topLevel)
    {
        if((features&ScalarFeatures.RecordHashing)==0)return false;var found=false;
        void Value(ScalarExpression? value){if(value is null)return;if(value.Operation=="call"&&value.Value==ObjectHashTarget)found=true;Value(value.Left);Value(value.Right);if(value.Arguments is not null)foreach(var arg in value.Arguments)Value(arg);}
        void Statements(IReadOnlyList<ScalarStatement> statements){foreach(var statement in statements){Value(statement.Expression);Value(statement.Increment);if(statement.Initializer is not null)Statements([statement.Initializer]);if(statement.Body is not null)Statements(statement.Body);if(statement.Alternative is not null)Statements(statement.Alternative);}}
        foreach(var function in topLevel.Concat(members).Concat(liftedFunctions!)){Statements(function.Body);foreach(var contract in function.Contracts)Value(contract.Expression);}
        if(!found||!requestedInstances!.Add("object-hash:"+activeStructs!.Count.ToString(CultureInfo.InvariantCulture)))return false;
        var at=new SourceLocation(1,1,0);var binder=new ExtendedBinder(new FunctionDeclaration("__object_hash",[],"int",[],[],new FunctionBody([]){ScalarSource=new("",at)},[],at),features,functions);
        var generated=binder.ObjectHashFunction();var previous=members.FindIndex(f=>f.Name==ObjectHashTarget);if(previous<0)members.Add(generated);else members[previous]=generated;
        return true;
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarExpression HashObject(ScalarExpression value,SourceLocation at)=>new("call",ScalarType.Int,at,ObjectHashTarget){Arguments=[IsObject(value.Type)?value:new("toobject",(ScalarType)ObjectTypeId,at,Left:value)]};
        public ScalarFunction ObjectHashFunction()
        {
            var at=_function.Location;var type=(ScalarType)ObjectTypeId;var self=new ScalarExpression("local",type,at,"__object");var body=new List<ScalarStatement>();
            body.Add(new("if",at,new("==",ScalarType.Bool,at,Left:self,Right:new("null",type,at)),Body:[new("return",at,new("literal",ScalarType.Int,at,"0"))]));
            foreach(var owner in _structs.Where(s=>s.IsClass&&!s.IsInterface&&!s.IsAbstract&&!s.IsTypeParameter&&!s.IsCheckInstance).ToArray())
            {
                if(owner.IsStringObject)
                {
                    body.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:self),Body:[new("return",at,HashRecordValue(StringObjectPayload(owner,self,at),at))]));continue;
                }
                if(Has(ScalarFeatures.Boxing)&&IsDelegate(owner.Id))
                {
                    body.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:self),Body:DelegateObjectHash(owner,self,at)));continue;
                }
                ScalarStructMethod? implementation=null;
                for(var k=IndexOf(owner.Id);k>=0&&implementation is null;k=_structs[k].BaseIndex)
                    implementation=_structs[k].Methods.FirstOrDefault(m=>RecordHashSignature(m)&&m.IsPublic&&!m.IsInternal&&!m.IsStatic&&(ObjectTextImplementation(m)||_structs[k].IsRecord));
                if(owner.BoxedType is {} payload)
                {
                    var stored=BoxPayload(owner,self,at);var method=IsStruct(payload)?ObjectOverride(StructOf(payload),RecordHashSignature):null;
                    var hash=method is null?HashRecordValue(stored,at):InterfaceImplementationCall(method,stored,[],ScalarType.Int,at);
                    body.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:self),Body:[new("return",at,hash)]));continue;
                }
                if(implementation is null)continue;
                var receiver=new ScalarExpression("downcast",_structs[implementation.Owner].Id,at,Left:self);
                var value=new ScalarExpression("call",ScalarType.Int,at,implementation.QualifiedName){Arguments=[receiver]};
                body.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:self),Body:[new("return",at,value)]));
            }
            body.Add(new("return",at,new("hash-identity",ScalarType.Int,at,Left:self)));
            return Erase(new(ObjectHashTarget,[new("__object",type)],ScalarType.Int,body,[TrueContract(at)],at));
        }
        private IReadOnlyList<ScalarStatement> DelegateObjectHash(ScalarStruct owner,ScalarExpression self,SourceLocation at)
        {
            var nodeName=Fresh();var hashName=Fresh();var partName=Fresh();
            var node=new ScalarExpression("local",owner.Id,at,nodeName);var hash=new ScalarExpression("local",ScalarType.Int,at,hashName);var part=new ScalarExpression("local",ScalarType.Int,at,partName);
            var headIndex=owner.Fields.ToList().FindIndex(f=>f.Name=="h");var tailIndex=owner.Fields.ToList().FindIndex(f=>f.Name=="t");var functionType=owner.Fields[headIndex].Type;
            var head=new ScalarExpression("field",functionType,at,headIndex.ToString(CultureInfo.InvariantCulture),Left:node);
            var loop=new List<ScalarStatement>{new("local",at,new("literal",ScalarType.Int,at,"0"),partName,ScalarType.Int){Mutable=true}};
            foreach(var callable in _structs.Where(s=>s.IsClass&&!s.IsAbstract&&!s.IsInterface&&!s.IsTypeParameter&&!s.IsCheckInstance&&Derives(_structs,IndexOf(s.Id),IndexOf(functionType))).ToArray())
            {
                var target=new ScalarExpression("downcast",callable.Id,at,Left:head);ScalarExpression value=new("literal",ScalarType.Int,at,((int)callable.Id).ToString(CultureInfo.InvariantCulture));
                for(var i=0;i<callable.Fields.Count;i++)
                {
                    var field=new ScalarExpression("field",callable.Fields[i].Type,at,i.ToString(CultureInfo.InvariantCulture),Left:target);
                    value=HashCombine(value,IsReference(field.Type)?new("hash-identity",ScalarType.Int,at,Left:field):HashRecordValue(field,at),at);
                }
                loop.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)callable.Id).ToString(CultureInfo.InvariantCulture),Left:head),Body:[new("expression",at,new("=",ScalarType.Int,at,Left:part,Right:value))]));
            }
            loop.Add(new("expression",at,new("=",ScalarType.Int,at,Left:hash,Right:HashCombine(hash,part,at))));
            loop.Add(new("expression",at,new("=",owner.Id,at,Left:node,Right:new("field",owner.Id,at,tailIndex.ToString(CultureInfo.InvariantCulture),Left:node))));
            return [new("local",at,new("downcast",owner.Id,at,Left:self),nodeName,owner.Id){Mutable=true},new("local",at,new("literal",ScalarType.Int,at,"0"),hashName,ScalarType.Int){Mutable=true},
                new("while",at,new("!=",ScalarType.Bool,at,Left:node,Right:new("null",owner.Id,at)),Body:loop),new("return",at,hash)];
        }
    }
}
