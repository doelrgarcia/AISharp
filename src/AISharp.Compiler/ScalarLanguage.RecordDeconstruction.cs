using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static string RecordPatternName(ScalarStruct owner,ScalarStructMethod method)
        =>"$record_pattern_"+StaticPropertyDeclaration(owner).ToString(CultureInfo.InvariantCulture)+"_"+method.QualifiedName[(owner.Name.Length+1)..];
    private static ScalarType DeconstructType(ScalarStruct owner,string text)
    {
        var binder=new ExtendedBinder(activeFeatures,"",owner.Location);
        binder.UseTypeArguments(owner.TypeArguments?.ToDictionary()??new(),owner.IsCheckInstance);
        return binder.DeconstructSignatureType(text,owner.Location);
    }
    private static string DeconstructSignature(ScalarStruct owner,ScalarStructMethod method)
    {
        var generic=ExtendedBinder.GenericOf(method.Declaration);
        return (generic?.Parameters.Count??0)+"|"+string.Join(";",method.Declaration.Parameters.Select(p=>(ParameterPassing(p.Type).Mode==ScalarParameterMode.Value?"v":"r")+":"+(generic is null?((int)DeconstructType(owner,p.Type)).ToString(CultureInfo.InvariantCulture):MethodSignatureType(method.Declaration,p.Type))));
    }
    private static ScalarStructMethod? FindBaseDeconstruct(int index,ScalarStruct owner,ScalarStructMethod method)
    {
        var signature=DeconstructSignature(owner,method);
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)
            foreach(var candidate in activeStructs![k].Methods)
                if(candidate.Name==method.Name&&DeconstructSignature(activeStructs[k],candidate)==signature)return candidate;
        return null;
    }
    private static ScalarStructMethod? FindDeconstructImplementation(int index,ScalarStructMethod root)
    {
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)
            foreach(var method in activeStructs![k].Methods)
                if(method.Name=="Deconstruct"&&method.Slot==root.Slot)return method;
        return null;
    }
    private static void AddRecordDeconstructionMethods(ScalarStruct owner,int index,List<ScalarStructMethod> methods)
    {
        var declared=methods.Where(m=>m.Name=="Deconstruct").ToList();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var method in declared)if(!seen.Add(DeconstructSignature(owner,method)))throw DeclarationError("AI3106","Duplicate Deconstruct parameter signature.",method.Declaration.Location);
        if(owner.IsRecord&&owner.Positional>0)
        {
            var conflicting=owner.Fields.FirstOrDefault(f=>f.Name=="Deconstruct");var conflictingProperty=owner.Properties.FirstOrDefault(p=>p.Name=="Deconstruct");
            if(conflicting is not null||conflictingProperty is not null)throw DeclarationError("AI3106","A record positional member cannot conflict with its Deconstruct method.",conflicting?.Location??conflictingProperty!.Location);
            var matching=declared.Where(m=>ExtendedBinder.GenericOf(m.Declaration)is null&&m.Declaration.Parameters.Count==owner.RecordParameterTypes.Count)
                .Where(m=>m.Declaration.Parameters.Select((p,i)=>ParameterPassing(p.Type).Mode!=ScalarParameterMode.Value&&DeconstructType(owner,p.Type)==owner.RecordParameterTypes[i]).All(v=>v)).FirstOrDefault();
            if(matching is not null)
            {
                if(matching.IsStatic||!matching.IsPublic||matching.Declaration.ReturnType is not ("unit" or "void")||matching.Declaration.Parameters.Any(p=>ParameterPassing(p.Type).Mode!=ScalarParameterMode.Out))
                    throw DeclarationError("AI3106","A primary-signature Deconstruct must be public, instance, return unit, and use out parameters.",matching.Declaration.Location);
            }
            else
            {
                var parameters=owner.RecordParameters.Select(p=>new ParameterDeclaration(p.Name,"out "+ParameterPassing(p.Type).Element)).ToArray();
                var body=string.Join("\n",owner.RecordParameters.Select(p=>p.Name+" = this."+p.Name))+"\nreturn";
                var declaration=new FunctionDeclaration("Deconstruct",parameters,"unit",[],[new(ContractKind.Ensures,"true",owner.Location)],new FunctionBody([]){ScalarSource=new(body,owner.Location)},[],owner.Location);
                var readonlyReceiver=!owner.IsClass&&owner.RecordParameters.All(p=>owner.Properties.FirstOrDefault(property=>property.Name==IdentifierName(p.Name)) is not {Getter:{ } getter}||methods.First(m=>m.QualifiedName==getter).IsReadonly);
                var method=new ScalarStructMethod("Deconstruct",owner.Name+(declared.Count==0?".Deconstruct":".$deconstruct_primary"),true,false,declaration){Owner=index,IsReadonly=readonlyReceiver,ReceiverMode=owner.IsClass?ScalarParameterMode.Value:readonlyReceiver?ScalarParameterMode.In:ScalarParameterMode.Ref};
                methods.Add(method);declared.Add(method);
            }
        }
        foreach(var method in declared.Where(m=>!m.IsStatic&&m.Declaration.ReturnType is "unit" or "void"&&ExtendedBinder.GenericOf(m.Declaration)is null&&m.Declaration.Parameters.All(p=>ParameterPassing(p.Type).Mode==ScalarParameterMode.Out)))
        {
            var outputs=method.Declaration.Parameters.Select((p,i)=>p with{Name="__record_value_"+i}).ToArray();
            var invoke="this.Deconstruct("+string.Join(", ",outputs.Select(p=>"out "+p.Name))+")\nreturn true";
            var wrapper=new FunctionDeclaration("__record_pattern",outputs,"bool",[],[new(ContractKind.Ensures,"true",owner.Location)],new FunctionBody([]){ScalarSource=new(invoke,owner.Location)},[],owner.Location);
            var name=RecordPatternName(owner,method);methods.Add(new(name,owner.Name+"."+name,false,false,wrapper){Owner=index,ReceiverMode=method.ReceiverMode,IsReadonly=method.IsReadonly});
        }
    }
    private sealed partial class ExtendedBinder
    {
        private readonly Dictionary<string,bool> _patternSubjectWritable=new(StringComparer.Ordinal);
        private (Local Local,string Operation) CapturePatternSubject(ScalarExpression subject)
        {
            var local=HiddenLocal(subject.Type);var operation="switch";
            if(Has(ScalarFeatures.RecordMembers)&&IsStruct(subject.Type)&&!IsClass(subject.Type))
            {
                var alias=subject.Operation=="local"&&subject.Value!=_this?.EmittedName&&IsReferenceStorage(subject)&&WritableAccessorReceiver(subject);
                local=local with{Mutable=true};_hiddenLocals[local.EmittedName]=local;_patternSubjectWritable[local.EmittedName]=true;
                if(alias)
                {
                    _referenceParameters[local.EmittedName]=ScalarParameterMode.Ref;_referenceContexts[local.EmittedName]=2;operation="switch-ref";
                }
                else operation="switch-copy";
            }
            return(local,operation);
        }
        private sealed record PatternValueCache(ScalarExpression Read,ScalarExpression Ensure);
        private sealed record DeconstructionCache(IReadOnlyList<ScalarExpression> Values,ScalarExpression Ensure);
        private readonly Dictionary<string,PatternValueCache> _patternValues=new(StringComparer.Ordinal);
        private readonly Dictionary<string,DeconstructionCache> _patternDeconstructions=new(StringComparer.Ordinal);
        private static string PatternKey(ScalarExpression value)
        {
            if(value.Operation=="upcast")return PatternKey(value.Left!);
            return System.Text.Json.JsonSerializer.Serialize(new object?[]{value.Operation,(int)value.Type,value.Value,value.Left is null?null:PatternKey(value.Left),value.Right is null?null:PatternKey(value.Right),value.Arguments?.Select(PatternKey).ToArray()});
        }
        private ScalarExpression PatternTemporary(ScalarType type,SourceLocation at)
        {
            var local=new Local($"__scalar_local_{_nextId++}",type,true);_hiddenLocals[local.EmittedName]=local;_assigned.Add(local.EmittedName);
            _pendingDeclarations.Add(new("local",at,DefaultValue(type,at),local.EmittedName,type){Mutable=true});
            return new("local",type,at,local.EmittedName);
        }
        private ScalarExpression PatternOnce(ScalarExpression flag,ScalarExpression operation,SourceLocation at)
            =>Fold(new("||",ScalarType.Bool,at,Left:flag,Right:And(operation,new("bindpat",ScalarType.Bool,at,Left:flag,Right:Fold(new("literal",ScalarType.Bool,at,"true"))),at)));
        private PatternResult Pattern(ScalarExpression subject,bool allowDiscard,bool allowType)
        {
            if(!Has(ScalarFeatures.RecordMembers)||subject.Operation=="local"||ConstantOf(subject)is not null)return PatternCore(subject,allowDiscard,allowType);
            var key=PatternKey(subject);
            if(!_patternValues.TryGetValue(key,out var cache))
            {
                var read=PatternTemporary(subject.Type,subject.Location);var flag=PatternTemporary(ScalarType.Bool,subject.Location);
                cache=new(read,PatternOnce(flag,new("bindpat",ScalarType.Bool,subject.Location,Left:read,Right:subject),subject.Location));_patternValues[key]=cache;
            }
            var pattern=PatternCore(cache.Read,allowDiscard,allowType);
            bool Uses(ScalarExpression value)=>value.Operation=="local"&&value.Value==cache.Read.Value||value.Left is not null&&Uses(value.Left)||value.Right is not null&&Uses(value.Right)||value.Arguments?.Any(Uses)==true;
            return Uses(pattern.Test)?pattern with{Test=And(cache.Ensure,pattern.Test,pattern.Location)}:pattern;
        }
        private PatternResult RecordPositionalPattern(ScalarExpression subject,ScalarExpression narrowed,ScalarExpression guard,ScalarStruct owner,Lexeme open)
        {
            var at=open.Location;var method=SelectPatternDeconstruct(owner,PositionalPatternArity(),at);
            var declaring=_structs[method.Owner];var wrapper=declaring.Methods.First(m=>m.Name==RecordPatternName(declaring,method));
            var key=PatternKey(subject)+"|"+wrapper.QualifiedName;
            if(!_patternDeconstructions.TryGetValue(key,out var cache))
            {
                var values=new List<ScalarExpression>();var arguments=new List<ScalarExpression>();
                foreach(var parameter in wrapper.Declaration.Parameters)
                {
                    var type=MemberType(declaring.TypeArguments,parameter.Type,at);var read=PatternTemporary(type,at);values.Add(read);arguments.Add(new("argument-out",type,at,Left:read));
                }
                var call=new ScalarExpression("call",ScalarType.Bool,at,wrapper.QualifiedName){Arguments=[AccessorReceiverArgument(wrapper,narrowed,at),..arguments]};
                cache=new(values,PatternOnce(PatternTemporary(ScalarType.Bool,at),call,at));_patternDeconstructions[key]=cache;
            }
            var count=0;var discards=true;var tests=new List<ScalarExpression>();
            if(Current.Text!=")")do
            {
                if(count>=cache.Values.Count)Fail("AI3106",$"'{owner.Name}' has {cache.Values.Count} positional member(s).",Current.Location);
                var inner=Pattern(cache.Values[count],true,true);discards&=inner.IsDiscard;tests.Add(inner.Test);count++;
            }while(Take(","));
            Expect(")");if(count!=cache.Values.Count)Fail("AI3106",$"'{owner.Name}' has {cache.Values.Count} positional member(s); the pattern has {count}.",at);
            var test=discards?guard:And(guard,cache.Ensure,at);foreach(var inner in tests)test=And(test,inner,at);
            if(Current.Text=="{"){test=PropertyPattern(narrowed,subject,Current,test).Test;discards=false;}
            if(IsIdentifier(Current.Text)&&Current.Text is not ("and" or "or" or "when" or "not")&&!IsReserved(Current.Text))
            {
                var name=Next();test=And(test,DeclarePatternVariable(name,owner.Id,narrowed),name.Location);
            }
            return new(test,null,false,at){Covers=discards?owner.Id:null};
        }
    }
}
