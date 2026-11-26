namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static string RecordApiType(ScalarStruct context,ScalarType type)
    {
        if((int)type>=StructTypeBase)
        {
            var info=activeStructs![IndexOf(type)];
            if(info.IsTypeParameter&&context.TypeArguments is not null)
                foreach(var pair in context.TypeArguments)if(pair.Value==type)return pair.Key;
            if(info.Template>=0)return activeStructs[info.Template].Name+"<"+string.Join(", ",info.TypeArgumentList.Select(t=>RecordApiType(context,t)))+">";
        }
        return ExtendedBinder.SignatureTypeName(type);
    }
    private static string RecordSelfType(ScalarStruct owner,ScalarStruct? context=null)=>RecordApiType(context??owner,owner.Id);
    private static bool RecordEqualityReceiver(ScalarStruct owner)
    {
        if(owner.IsRecord)return true;
        if(!owner.IsTypeParameter)return false;
        for(var k=owner.BaseIndex;k>=0;k=activeStructs![k].BaseIndex)if(activeStructs![k].IsRecord)return true;
        return false;
    }
    private static bool LazyRecordApi(ScalarStructMethod method)=>method.RecordEqualityApi is not null||method.RecordDisplayApi is not null||method.IsRecordHashApi||method.IsRecordCustomEquality;
    private static ScalarStructMethod? FindBaseRecordEquality(int index,ScalarStruct owner,ScalarStructMethod method)
    {
        var signature=DeconstructSignature(owner,method);
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)
            foreach(var candidate in activeStructs![k].Methods)
                if(candidate.Name=="Equals"&&DeconstructSignature(activeStructs[k],candidate)==signature)return candidate;
        return null;
    }
    private static void EnsureRecordEqualityApis()
    {
        if((activeFeatures&ScalarFeatures.RecordInheritance)==0)return;
        foreach(var owner in activeStructs!.ToArray())
        {
            if(!owner.IsRecord||owner.IsTypeParameter||owner.Template>=0&&owner.Template!=IndexOf(owner.Id))continue;
            var index=IndexOf(owner.Id);var methods=owner.Methods.ToList();var suffix=StaticPropertyDeclaration(owner).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach(var field in owner.Fields.Where(f=>f.Owner==index&&!f.Hidden).Concat(owner.StaticFields.Where(f=>!f.Hidden)))
                if(field.Name is "Clone" or "Equals")throw DeclarationError("AI3106",field.Name=="Clone"?"A record member cannot be named Clone.":"A field cannot replace the compiler-provided record Equals methods.",field.Location);
            foreach(var property in owner.Properties)
                if(property.Name is "Clone" or "Equals")throw DeclarationError("AI3106",property.Name=="Clone"?"A record member cannot be named Clone.":"A property cannot replace the compiler-provided record Equals methods.",property.Location);
            foreach(var method in methods)
                if(method.Name=="Clone")throw DeclarationError("AI3106","A record member cannot be named Clone.",method.Declaration.Location);
            void Add(string kind,string parameter,string target,bool isVirtual=false,bool isOverride=false,bool isSealed=false)
            {
                if(methods.Any(m=>m.QualifiedName==owner.Name+"."+target))return;
                var at=owner.Location;var declaration=new FunctionDeclaration("__record_api_equals",[new("__record_other",parameter)],"bool",[],[new(ContractKind.Ensures,"true",at)],new FunctionBody([]){ScalarSource=new("",at)},[],at);
                methods.Add(new("Equals",owner.Name+"."+target,true,false,declaration){Owner=index,IsVirtual=isVirtual,IsOverride=isOverride,IsSealed=isSealed,RecordEqualityApi=kind});
            }
            if(CustomRecordEquality(owner)is null)Add("typed",RecordSelfType(owner),"$record_equals_"+suffix,owner.IsClass&&!owner.IsSealed);
            if(methods.Any(m=>m.RecordEqualityApi is null&&m.Name=="Equals"&&m.Declaration.Parameters.Count==1&&ParameterPassing(m.Declaration.Parameters[0].Type).Mode==ScalarParameterMode.Value&&m.Declaration.Parameters[0].Type.Trim()=="object"))
                throw DeclarationError("AI3106","A record cannot explicitly declare its object Equals override.",methods.First(m=>m.Name=="Equals"&&m.Declaration.Parameters.Count==1&&m.Declaration.Parameters[0].Type.Trim()=="object").Declaration.Location);
            Add("object","object","$record_object_equals_"+suffix,owner.IsClass&&owner.BaseIndex<0&&!owner.IsSealed,owner.IsClass&&owner.BaseIndex>=0);
            if(owner.IsClass&&owner.BaseIndex>=0)
            {
                var parent=activeStructs[owner.BaseIndex];var parentType=RecordSelfType(parent,owner);
                if(methods.Any(m=>m.RecordEqualityApi is null&&m.Name=="Equals"&&m.Declaration.Parameters.Count==1&&DeconstructType(owner,m.Declaration.Parameters[0].Type)==parent.Id))
                    throw DeclarationError("AI3106","The inherited record Equals bridge is compiler-provided and sealed.",methods.First(m=>m.Name=="Equals"&&m.Declaration.Parameters.Count==1&&DeconstructType(owner,m.Declaration.Parameters[0].Type)==parent.Id).Declaration.Location);
                Add("bridge",parentType,"$record_base_equals_"+suffix,isOverride:true,isSealed:true);
            }
            var seen=new HashSet<string>(System.StringComparer.Ordinal);
            foreach(var method in methods.Where(m=>m.Name=="Equals"))if(!seen.Add(DeconstructSignature(owner,method)))throw DeclarationError("AI3106","Duplicate Equals parameter signature.",method.Declaration.Location);
            activeStructs[index]=owner with{Methods=methods};
        }
    }
    private static bool EmitRecordApis(List<ScalarFunction> members,ScalarFeatures features,IReadOnlyList<FunctionDeclaration> functions,IReadOnlyList<ScalarFunction> topLevel)
    {
        if((features&(ScalarFeatures.RecordInheritance|ScalarFeatures.RecordDisplay|ScalarFeatures.RecordHashing))==0)return false;var calls=new HashSet<string>(System.StringComparer.Ordinal);var emitted=false;
        void Value(ScalarExpression? value)
        {
            if(value is null)return;if(value.Operation=="call"&&value.Value is not null)calls.Add(value.Value);
            Value(value.Left);Value(value.Right);if(value.Arguments is not null)foreach(var argument in value.Arguments)Value(argument);
        }
        void Statements(IReadOnlyList<ScalarStatement> statements)
        {
            foreach(var statement in statements){Value(statement.Expression);Value(statement.Increment);if(statement.Initializer is not null)Statements([statement.Initializer]);if(statement.Body is not null)Statements(statement.Body);if(statement.Alternative is not null)Statements(statement.Alternative);}
        }
        foreach(var function in topLevel.Concat(members).Concat(liftedFunctions!)){Statements(function.Body);foreach(var contract in function.Contracts)Value(contract.Expression);}
        foreach(var owner in activeStructs!.ToArray())foreach(var method in owner.Methods.Where(LazyRecordApi).ToArray())
        {
            if(owner.IsCheckInstance||owner.IsTypeParameter)continue;
            if((method.RecordEqualityApi is not null||method.RecordDisplayApi is not null||method.IsRecordHashApi)&&calls.Contains(method.QualifiedName)&&requestedInstances!.Add("api:"+method.QualifiedName))
            {members.Add(new ExtendedBinder(method.Declaration,features,functions,owner,method,false).Bind());emitted=true;}
            if(method.Slot==method.QualifiedName&&calls.Contains(method.Slot+".virtual")&&requestedInstances!.Add("api-dispatch:"+method.Slot))
            {members.Add(new ExtendedBinder(method.Declaration,features,functions,owner,method,false).Dispatch(method));emitted=true;}
        }
        return emitted;
    }
    private sealed partial class ExtendedBinder
    {
        private readonly string? _recordEqualityApi;
        private List<ScalarStatement> BindRecordEqualityApi()
        {
            var owner=_struct!;var at=owner.Location;var input=LocalNamed("__record_other")!;
            var self=new ScalarExpression("local",owner.Id,at,_this!.EmittedName);var other=new ScalarExpression("local",input.Type,at,input.EmittedName);ScalarExpression value;
            if(owner.IsClass)value=new(_recordEqualityApi=="typed"?"record-impl-equals":"record-equals",ScalarType.Bool,at,Left:self,Right:other);
            else if(_recordEqualityApi=="object"&&Has(ScalarFeatures.Boxing))
            {
                var box=ValueBox(owner.Id,at);
                value=And(new("isexact",ScalarType.Bool,at,((int)box.Id).ToString(System.Globalization.CultureInfo.InvariantCulture),Left:other),RecordEquality("==",self,UnboxValue(other,owner.Id,at),at,comparer:true),at);
            }
            else value=_recordEqualityApi=="object"?new("literal",ScalarType.Bool,at,"false"):FieldsEqual(owner,self,other,at);
            return [new("return",at,value)];
        }
    }
}
