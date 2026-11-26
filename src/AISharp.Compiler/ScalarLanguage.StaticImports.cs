using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private sealed record ImportedStatic(string Target,ScalarType Type,bool Method,bool Enumeration=false,bool Generic=false,int ImplicitInParameters=0);
        private List<ImportedStatic> StaticImportCandidates(Lexeme member)
        {
            var result=new List<ImportedStatic>();
            if(!Has(ScalarFeatures.StaticImports)||activeCompilation?.StaticImportResolver is null||!CanBindTypeParameter(member.Text)
                ||IsLocalName(member.Text)||ScopedTypeArgument(member.Text,out _)||ScopedCompilationFunction(member.Text,member.Location) is not null
                ||_struct is not null&&NameMemberExists(_struct.Id,member.Name)&&CompilationAccessibleMember(member.Name))return result;
            bool Accessible(ScalarStruct owner,bool visible,bool assembly=false,bool inherited=false)=>visible||assembly||SameTypeScope(owner)||inherited&&_struct is not null&&Derives(_structs,IndexOf(_struct.Id),IndexOf(owner.Id));
            foreach(var scope in activeCompilation.StaticImportResolver(member.Location.Offset))
            {
                var position=Current.Location.Offset==member.Location.Offset?_position:Math.Max(0,_position-1);
                if(scope.Declarations.Contains((member.Name,CompilationArity(position+1))))return [];
                foreach(var target in scope.Targets)
                {
                var type=Type(target,member.Location,allowStatic:true);var name=member.Name;
                if(IsEnum(type)){if(EnumOf(type).Members.Any(m=>m.Key==name))result.Add(new(target,type,false,true));continue;}
                if(ScalarConstants.IsNative(type))
                {if(name is "MinValue" or "MaxValue" or "Size")result.Add(new(target,type,false));continue;}
                if(IsObject(type))
                {if(name is "Equals" or "ReferenceEquals")result.Add(new(target,type,true));continue;}
                if(!IsStruct(type))continue;var owner=StructOf(type);var index=IndexOf(type);
                if(owner.Methods.Any(m=>m.Owner==index&&m.IsStatic&&!m.IsAccessor&&!m.IsConstructor&&!m.IsStaticConstructor&&m.Name==name&&Accessible(owner,m.IsPublic,m.IsInternal,m.IsProtectedMember)))result.Add(new(target,type,true));
                else if(owner.StaticFields.Any(f=>f.Owner==index&&!f.Hidden&&f.Name==name&&Accessible(owner,f.IsPublic,f.IsInternal))
                    ||owner.Properties.Any(p=>p.Owner==index&&p.IsStatic&&p.Name==name&&Accessible(owner,p.IsPublic,p.IsInternal,p.IsProtectedMember)))result.Add(new(target,type,false));
                }
                if(result.Count>0)break;
            }
            if(member.Text=="nameof"&&TokenAt(Current.Location.Offset==member.Location.Offset?_position+1:_position).Text=="("&&result.All(c=>!c.Method))
            {
                var callable=result.Any(c=>!c.Enumeration&&IsStruct(c.Type)&&(FindStaticField(IndexOf(c.Type),member.Name) is {} field&&IsDelegate(field.Type)||FindProperty(IndexOf(c.Type),member.Name) is {} property&&IsDelegate(property.Type)));
                if(!callable)return [];
            }
            return result.DistinctBy(x=>(x.Type,x.Method,x.Enumeration)).ToList();
        }
        private ScalarExpression StaticImportedExpression(Lexeme member,List<ImportedStatic> candidates)
        {
            Next();member=member with{Text=member.Name};
            if(candidates.Count>1)
            {
                if(candidates.Any(c=>!c.Method))Fail("AI3106","Ambiguous imported static member '"+member.Text+"'.",member.Location);
                if(Current.Text=="("||Current.Text=="<"&&!MethodGroupAhead())return StaticImportedCall(member,candidates);
                var first=MethodGroupExpression(member,StaticType(StructOf(candidates[0].Type),member.Location));
                return new("import-methodgroup",(ScalarType)MethodGroupTypeId,member.Location,member.Text){Arguments=candidates.Select(c=>first with{Left=StaticType(StructOf(c.Type),member.Location)}).ToArray()};
            }
            var chosen=candidates[0];
            if(chosen.Enumeration)
            {
                var value=EnumOf(chosen.Type).Members.Single(m=>m.Key==member.Text).Value;
                return Fold(new("literal",chosen.Type,member.Location,value.ToString(CultureInfo.InvariantCulture)));
            }
            if(ScalarConstants.IsNative(chosen.Type))return new("native-info",member.Text=="Size"?ScalarType.Int:chosen.Type,member.Location,member.Text+":"+ScalarConstants.Spelling(chosen.Type));
            if(IsObject(chosen.Type))
            {
                var arguments=ObjectArguments(member,2);
                return member.Text=="Equals"?ObjectEquals(arguments[0],arguments[1],member.Location):new("==",ScalarType.Bool,member.Location,Left:arguments[0],Right:arguments[1]);
            }
            var receiver=StaticType(StructOf(chosen.Type),member.Location);
            if(chosen.Method)return Current.Text=="("||Current.Text=="<"&&!MethodGroupAhead()?MethodCall(receiver,member):MethodGroupExpression(member,receiver);
            return StaticFieldRead(StructOf(chosen.Type),member);
        }
        private ScalarExpression StaticImportedCall(Lexeme member,List<ImportedStatic> candidates)
        {
            List<(ScalarType Type,SourceLocation Location)>? explicitTypes=null;
            if(Take("<")){explicitTypes=[];do{var written=TypeToken();explicitTypes.Add((Type(written.Text,written.Location),written.Location));}while(Take(","));Expect(">");}
            Expect("(");var arguments=new List<ScalarExpression>();if(!Take(")")){do{arguments.Add(CallArgument());}while(Take(","));Expect(")");}
            var applicable=new List<(ImportedStatic Candidate,IReadOnlyList<ScalarType> Parameters)>();
            foreach(var candidate in candidates)
            {
                var owner=StructOf(candidate.Type);var method=FindMethod(IndexOf(owner.Id),member.Text)!;
                if(method.Declaration.Parameters.Count!=arguments.Count)continue;
                var generic=GenericOf(method.Declaration);if(generic is null&&explicitTypes is not null)continue;
                try
                {
                    var map=generic is null?owner.TypeArguments:BindGenericArguments(member,method.Declaration,generic,explicitTypes,arguments,owner);
                    var parameters=method.Declaration.Parameters.Select(p=>MemberType(map,p.Type,member.Location)).ToArray();
                    if(arguments.Select((a,i)=>ImportedArgumentApplicable(a,parameters[i],ParameterPassing(method.Declaration.Parameters[i].Type).Mode)).All(x=>x))
                        applicable.Add((candidate with{Generic=generic is not null,ImplicitInParameters=method.Declaration.Parameters.Select((p,i)=>ParameterPassing(p.Type).Mode==ScalarParameterMode.In&&!ReferenceArgument(arguments[i])?1:0).Sum()},parameters));
                }
                catch(ScalarError error)when(error.Diagnostic.Code is "AI3116" or "AI3107"){}
            }
            var selected=BestImportedCandidate(applicable,arguments,member.Location);
            var declaring=StructOf(selected.Type);var chosen=FindMethod(IndexOf(selected.Type),member.Text)!;var receiver=StaticType(declaring,member.Location);
            return GenericOf(chosen.Declaration) is {} definition?GenericCall(member,chosen.Declaration,definition,explicitTypes,arguments,declaring,chosen,receiver):MethodCall(receiver,member,given:arguments);
        }
        private bool ImportedArgumentApplicable(ScalarExpression argument,ScalarType type,ScalarParameterMode mode)
        {
            if(argument.Operation=="out-variable"||argument.Operation=="argument-out"&&argument.Type==ScalarType.Unit)return mode==ScalarParameterMode.Out;
            if(ReferenceArgument(argument))return mode!=ScalarParameterMode.Value&&(argument.Operation==(mode==ScalarParameterMode.Out?"argument-out":mode==ScalarParameterMode.Ref?"argument-ref":"argument-in")||mode==ScalarParameterMode.In&&argument.Operation=="argument-ref")&&argument.Type==type;
            return mode is not (ScalarParameterMode.Ref or ScalarParameterMode.Out)&&TryCoerce(argument,type) is not null;
        }
        private ImportedStatic BestImportedCandidate(List<(ImportedStatic Candidate,IReadOnlyList<ScalarType> Parameters)> choices,IReadOnlyList<ScalarExpression> arguments,SourceLocation at)
        {
            if(choices.Count==0){Fail("AI3116","No imported static overload is applicable.",at);return null!;}
            bool Better((ImportedStatic Candidate,IReadOnlyList<ScalarType> Parameters) a,(ImportedStatic Candidate,IReadOnlyList<ScalarType> Parameters) b)
            {
                var left=a.Parameters;var right=b.Parameters;
                var strict=false;
                for(var i=0;i<left.Count;i++)
                {
                    if(left[i]==right[i])continue;
                    if(arguments[i].Operation=="out-variable"||arguments[i].Operation=="argument-out"&&arguments[i].Type==ScalarType.Unit)continue;
                    if(left[i]==arguments[i].Type){strict=true;continue;}
                    if(right[i]==arguments[i].Type)return false;
                    var l=TryCoerce(new("default",left[i],at),right[i]) is not null;var r=TryCoerce(new("default",right[i],at),left[i]) is not null;
                    if(!l||r)return false;strict=true;
                }
                return strict||!a.Candidate.Generic&&b.Candidate.Generic||a.Candidate.Generic==b.Candidate.Generic&&a.Candidate.ImplicitInParameters<b.Candidate.ImplicitInParameters;
            }
            var best=choices.Where(x=>choices.All(y=>x.Candidate==y.Candidate||Better(x,y))).ToArray();
            if(best.Length!=1){Fail("AI3106","Ambiguous imported static overload.",at);return null!;}return best[0].Candidate;
        }
        private ScalarExpression SelectImportedMethodGroup(ScalarExpression group,ScalarType target)
        {
            var signature=DelegateOf(target);var arguments=signature.Parameters.Select(p=>new ScalarExpression("default",Type(p.Type,group.Location),group.Location)).ToArray();
            var choices=new List<(ImportedStatic Candidate,IReadOnlyList<ScalarType> Parameters)>();
            foreach(var item in group.Arguments!)
            {
                var owner=StructOf(item.Left!.Type);var method=FindMethod(IndexOf(owner.Id),item.Value!)!;
                if(method.Declaration.Parameters.Count!=arguments.Length)continue;
                try
                {
                    var map=MethodGroupArguments(method.Declaration,owner,item,arguments.Select(a=>a.Type).ToArray(),out _);
                    var parameters=method.Declaration.Parameters.Select(p=>MemberType(map,p.Type,group.Location)).ToArray();
                    if(arguments.Select((a,i)=>ParameterPassing(method.Declaration.Parameters[i].Type).Mode==ParameterPassing(signature.Parameters[i].Type).Mode&&TryCoerce(a,parameters[i]) is not null).All(x=>x))choices.Add((new(owner.Name,owner.Id,true,Generic:GenericOf(method.Declaration) is not null),parameters));
                }
                catch(ScalarError error)when(error.Diagnostic.Code is "AI3116" or "AI3107"){}
            }
            var selected=BestImportedCandidate(choices,arguments,group.Location);return group.Arguments!.Single(x=>x.Left!.Type==selected.Type);
        }
    }
}
