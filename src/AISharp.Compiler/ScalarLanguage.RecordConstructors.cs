using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<string,(string Target,SourceLocation At)>? recordConstructorEdges;
    public static string RecordConstructorInitName(ScalarStructMethod method)=>method.QualifiedName+"$init";
    private static bool RecordConstructorsEnabled(ScalarStruct owner)=>owner.IsRecord&&(activeFeatures&ScalarFeatures.RecordConstructors)!=0;
    private static ScalarStruct RecordConstructorDeclaration(ScalarStruct owner)=>owner.Template>=0?activeStructs![owner.Template]:owner;
    private static string RecordConstructorSignature(ScalarStruct owner,ScalarStructMethod method)=>DeconstructSignature(RecordConstructorDeclaration(owner),method);
    private static bool RecordCopySignature(ScalarStruct owner,ScalarStructMethod method)
    {
        var declaration=RecordConstructorDeclaration(owner);
        return owner.IsClass&&method.Declaration.Parameters.Count==1&&ParameterPassing(method.Declaration.Parameters[0].Type).Mode==ScalarParameterMode.Value&&DeconstructType(declaration,method.Declaration.Parameters[0].Type)==declaration.Id;
    }
    private static void AddRecordConstructors(ScalarStruct owner,int index,List<ScalarStructMethod> methods)
    {
        if(!RecordConstructorsEnabled(owner))return;
        var source=methods.Where(m=>m.IsConstructor).ToArray();
        foreach(var method in source)
        {
            if(method.RecordConstructorKind=="copy"&&!owner.IsSealed&&!method.IsPublic&&!method.IsProtectedConstructor)
                throw DeclarationError("AI3106","A copy constructor of an unsealed record must be public or protected.",method.Declaration.Location);
            if(method.IsProtectedConstructor&&!owner.IsClass)throw DeclarationError("AI3106","A struct constructor cannot be protected.",method.Declaration.Location);
        }
        void Add(string kind,IReadOnlyList<ParameterDeclaration> parameters,bool isPublic)
        {
            var name=kind=="primary"||kind=="default"?RecordFactoryName(owner):owner.Name+".$record_"+kind+"_"+StaticPropertyDeclaration(owner).ToString(CultureInfo.InvariantCulture);
            var declaration=new FunctionDeclaration("new",parameters,RecordSelfType(owner),[],[],new FunctionBody([]){ScalarSource=new("",owner.Location)},[],owner.Location);
            var synthesized=new ScalarStructMethod("new",name,isPublic,true,declaration){Owner=index,RecordConstructorKind=kind,IsLifted=kind!="copy",IsSynthesizedRecordCopy=kind=="copy",IsProtectedConstructor=kind=="copy"&&!owner.IsSealed||kind=="default"&&owner.IsAbstract};
            if(kind=="primary"&&source.FirstOrDefault(m=>RecordConstructorSignature(owner,m)==RecordConstructorSignature(owner,synthesized)) is { } duplicate)
                throw DeclarationError("AI3106","A declared constructor duplicates the primary constructor signature.",duplicate.Declaration.Location);
            methods.Add(synthesized);
        }
        if(owner.HasRecordPrimaryConstructor)Add("primary",owner.RecordParameters,true);
        else if(owner.IsClass&&source.Length==0)Add("default",[],!owner.IsAbstract);
        if(!owner.IsClass&&!methods.Any(m=>m.IsConstructor&&m.Declaration.Parameters.Count==0))Add("zero",[],true);
        if(owner.IsClass&&!source.Any(m=>m.RecordConstructorKind=="copy"))Add("copy",[new("other",RecordSelfType(RecordConstructorDeclaration(owner)))],false);
        var signatures=new HashSet<string>(StringComparer.Ordinal);
        foreach(var method in methods.Where(m=>m.IsConstructor))
            if(!signatures.Add(RecordConstructorSignature(owner,method)))throw DeclarationError("AI3106","Duplicate constructor parameter signature.",method.Declaration.Location);
    }
    private static void ValidateRecordConstructorCycles()
    {
        var edges=recordConstructorEdges;if(edges is null)return;
        var visiting=new HashSet<string>(StringComparer.Ordinal);var done=new HashSet<string>(StringComparer.Ordinal);
        void Visit(string source)
        {
            if(done.Contains(source)||!edges.TryGetValue(source,out var edge))return;
            if(!visiting.Add(source))throw DeclarationError("AI3106","A constructor initializer cannot form a cycle.",edge.At);
            Visit(edge.Target);visiting.Remove(source);done.Add(source);
        }
        foreach(var source in edges.Keys)Visit(source);
    }
    private sealed partial class ExtendedBinder
    {
        private readonly ScalarStructMethod? _recordConstructor;
        private bool AccessibleRecordConstructor(ScalarStruct owner,ScalarStructMethod method,bool baseCall)
            =>method.IsPublic||method.IsInternalConstructor||SameTypeScope(owner)||baseCall&&method.IsProtectedConstructor&&_struct is not null&&Derives(_structs,IndexOf(_struct.Id),IndexOf(owner.Id));
        private ScalarStructMethod SelectRecordConstructor(ScalarStruct owner,IReadOnlyList<ScalarExpression> arguments,SourceLocation at,bool baseCall=false,bool copyOnly=false)
        {
            var all=owner.Methods.Where(m=>m.IsConstructor&&(!copyOnly||m.RecordConstructorKind=="copy")).ToList();
            if(!_checkOnly&&(_typeArgs is not null||_struct?.TypeArguments is not null)&&checkedDeconstructSelections!.TryGetValue(DeconstructBindingSite(at),out var selected))
            {
                var rebound=all.SingleOrDefault(m=>(owner.Template>=0?owner.Template:IndexOf(owner.Id))==selected.Template&&m.QualifiedName[(owner.Name.Length+1)..]==selected.Suffix);
                if(rebound is null||!ApplicableDeconstruct(rebound,arguments))Fail("AI3107","The closed constructor call does not preserve its declaration binding.",at);
                return rebound!;
            }
            var matching=all.Where(m=>ApplicableDeconstruct(m,arguments)).ToList();
            var candidates=matching.Where(m=>AccessibleRecordConstructor(owner,m,baseCall)).ToList();
            if(candidates.Count==0)
            {
                if(matching.Count>0)Fail("AI3119",$"A matching constructor of '{owner.Name}' is inaccessible.",at);
                if(!copyOnly)
                {
                    var ordinary=all.Where(m=>m.RecordConstructorKind!="copy"&&m.Declaration.Parameters.Count==arguments.Count&&AccessibleRecordConstructor(owner,m,true)).ToArray();
                    if(ordinary.Length==1)BindRecordConstructorArguments(owner,ordinary[0],arguments.ToList(),at);
                }
                Fail("AI3116",$"No constructor of '{owner.Name}' accepts these arguments.",at);
            }
            if(candidates.Count>1)candidates=candidates.Where(c=>!candidates.Any(other=>!ReferenceEquals(c,other)&&BetterDeconstruct(other,c,arguments))).ToList();
            if(candidates.Count!=1)Fail("AI3106",$"The constructor call on '{owner.Name}' is ambiguous.",at);
            return RememberDeconstruct(candidates[0],at);
        }
        private List<ScalarExpression> BindRecordConstructorArguments(ScalarStruct owner,ScalarStructMethod method,List<ScalarExpression> arguments,SourceLocation at)
        {
            for(var i=0;i<arguments.Count;i++)arguments[i]=BindArgument(arguments[i],MemberType(owner.TypeArguments,method.Declaration.Parameters[i].Type,at),ParameterPassing(method.Declaration.Parameters[i].Type).Mode);
            CompleteArguments(arguments);return arguments;
        }
        private ScalarExpression RecordConstructorCall(ScalarStruct owner,ScalarStructMethod method,ScalarExpression? self,List<ScalarExpression> arguments,SourceLocation at)
        {
            BindRecordConstructorArguments(owner,method,arguments,at);
            if(self is null)
            {
                if(method.RecordConstructorKind=="zero")return new("new",owner.Id,at);
                return new("call",owner.Id,at,method.QualifiedName){Arguments=arguments};
            }
            if(method.RecordConstructorKind=="zero")return new("new",owner.Id,at);
            var name=method.RecordConstructorKind is "primary" or "default"?owner.Name+"."+RecordInitializerName(owner):RecordConstructorInitName(method);
            return new("call",owner.Id,at,name){Arguments=[Upcast(self,owner.Id),..arguments]};
        }
        private List<ScalarStatement> RecordConstructorPrefix()
        {
            var owner=_struct!;var method=_recordConstructor!;var at=method.Declaration.Location;
            var self=new ScalarExpression("local",owner.Id,at,_this!.EmittedName);var result=new List<ScalarStatement>();
            NewLines();var initializerAt=Current.Location;
            var explicitCall=Current.Text is "this" or "base"&&TokenAt(_position+1).Text=="."&&TokenAt(_position+2).Text=="new";
            var toThis=explicitCall&&Current.Text=="this";var arguments=new List<ScalarExpression>();
            if(explicitCall)
            {
                Next();Next();Next();Expect("(");_inBaseArgs=true;
                try{if(!Take(")")){do{arguments.Add(CallArgument());}while(Take(","));Expect(")");}}
                finally{_inBaseArgs=false;}
                EndStatement();NewLines();
            }
            if(explicitCall&&!toThis&&!owner.IsClass)Fail("AI3106","A struct constructor cannot invoke a base constructor.",initializerAt);
            if(method.RecordConstructorKind=="copy")
            {
                if(toThis)Fail("AI3106","A record class copy constructor must invoke a base copy constructor.",initializerAt);
                var other=LocalNamed(method.Declaration.Parameters[0].Name)!;
                var value=new ScalarExpression("local",other.Type,at,other.EmittedName);
                if(owner.BaseIndex>=0)
                {
                    if(method.IsSynthesizedRecordCopy)arguments=[value];
                    else if(!explicitCall)Fail("AI3106","A derived record copy constructor must explicitly invoke its base copy constructor.",initializerAt);
                    var parent=_structs[owner.BaseIndex];var target=SelectRecordConstructor(parent,arguments,explicitCall?initializerAt:at,true,true);
                    if(!method.IsSynthesizedRecordCopy&&target.IsSynthesizedRecordCopy&&RequiredMemberNames(parent).Any())Fail("AI3106","A constructor chaining to a required-member copy constructor must explicitly declare SetsRequiredMembers.",method.Declaration.Location);
                    result.Add(new("expression",at,RecordConstructorCall(parent,target,self,arguments,at)));
                }
                else if(explicitCall&&arguments.Count!=0)Fail("AI3116","The object base constructor has no parameters.",initializerAt);
                if(method.IsSynthesizedRecordCopy)
                    for(var i=0;i<owner.Fields.Count;i++)
                    {
                        var field=owner.Fields[i];if(field.Owner!=IndexOf(owner.Id))continue;
                        result.Add(new("expression",at,new("=",field.Type,at,Left:new("field",field.Type,at,i.ToString(CultureInfo.InvariantCulture),Left:self),Right:new("field",field.Type,at,i.ToString(CultureInfo.InvariantCulture),Left:value))));
                    }
                return result;
            }
            if(toThis)
            {
                var target=SelectRecordConstructor(owner,arguments,initializerAt);
                if(owner.HasRecordPrimaryConstructor&&target.RecordConstructorKind=="zero")Fail("AI3106","A positional record struct constructor must invoke its primary constructor or an explicitly declared constructor.",initializerAt);
                if(target.IsSynthesizedRecordCopy&&RequiredMemberNames(owner).Any())Fail("AI3106","A constructor chaining to a required-member copy constructor must explicitly declare SetsRequiredMembers.",method.Declaration.Location);
                recordConstructorEdges![method.QualifiedName]=(target.QualifiedName,initializerAt);
                var call=RecordConstructorCall(owner,target,self,arguments,initializerAt);
                result.Add(new("expression",initializerAt,owner.IsClass?call:new("=",owner.Id,initializerAt,Left:self,Right:call)));
                if(target.RecordConstructorKind=="zero")result.AddRange(BindInstanceInitializers());
                return result;
            }
            if(owner.HasRecordPrimaryConstructor)Fail("AI3106","A non-copy constructor of a positional record must invoke another constructor of the same type.",initializerAt);
            result.AddRange(BindInstanceInitializers());
            if(owner.BaseIndex>=0)
            {
                var parent=_structs[owner.BaseIndex];var target=SelectRecordConstructor(parent,arguments,explicitCall?initializerAt:at,true);
                result.Add(new("expression",at,RecordConstructorCall(parent,target,self,arguments,at)));
            }
            else if(explicitCall&&arguments.Count!=0)Fail("AI3116","The object base constructor has no parameters.",initializerAt);
            return result;
        }
        private ScalarExpression NewRecordWithConstructors(ScalarStruct owner,Lexeme token,SourceLocation at)
        {
            if(owner.IsAbstract||owner.IsInterface)Fail("AI3116",$"Cannot create an instance of the abstract type '{owner.Name}'.",token.Location);
            var arguments=new List<ScalarExpression>();
            if(Take("(")){if(!Take(")")){do{arguments.Add(CallArgument());}while(Take(","));Expect(")");}}
            else if(Current.Text!="{")Fail("AI3106","Expected '(' or '{' after the record type.");
            var selected=SelectRecordConstructor(owner,arguments,token.Location);var created=RecordConstructorCall(owner,selected,null,arguments,at);
            return RequiredObjectInitializer(owner,created,token.Location,enforceRequired:Has(ScalarFeatures.RequiredMembers)&&!selected.IsSynthesizedRecordCopy);
        }
    }
}
