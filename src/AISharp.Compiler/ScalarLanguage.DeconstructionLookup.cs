namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record DeconstructSelection(int Template,string Suffix);
    [ThreadStatic] private static Dictionary<(int Function,string Scope,int Call),DeconstructSelection>? checkedDeconstructSelections;
    private sealed partial class ExtendedBinder
    {
        public ScalarType DeconstructSignatureType(string text,SourceLocation at)=>Type(text,at);
        private List<ScalarStructMethod> DeconstructMethods(ScalarStruct owner,string name="Deconstruct")
        {
            var result=new List<ScalarStructMethod>();var signatures=new HashSet<string>(StringComparer.Ordinal);
            for(var k=IndexOf(owner.Id);k>=0;k=_structs[k].BaseIndex)
            {
                var own=_structs[k].Methods.Where(m=>m.Name==name&&!m.IsAccessor&&!m.IsConstructor).ToArray();
                foreach(var method in own)if(!signatures.Contains(DeconstructSignature(_structs[method.Owner],method)))result.Add(method);
                foreach(var method in own)signatures.Add(DeconstructSignature(_structs[method.Owner],method));
            }
            return result;
        }
        private bool AccessibleDeconstruct(ScalarStructMethod method)=>method.IsPublic||SameTypeScope(_structs[method.Owner])||method.IsProtectedMember&&_struct is not null&&Derives(_structs,IndexOf(_struct.Id),method.Owner);
        private List<ScalarStructMethod> MostDerivedDeconstruct(ScalarStruct owner,List<ScalarStructMethod> candidates)
        {
            int LookupOwner(ScalarStructMethod method)=>method.IsOverride&&method.Slot is { } slot?_structs.FindIndex(s=>s.Methods.Any(m=>m.QualifiedName==slot)):method.Owner;
            for(var k=IndexOf(owner.Id);k>=0;k=_structs[k].BaseIndex)
            {
                var level=candidates.Where(m=>LookupOwner(m)==k).ToList();if(level.Count>0)return level;
            }
            return candidates;
        }
        private IReadOnlyDictionary<string,ScalarType>? DeconstructArguments(ScalarStructMethod method,IReadOnlyList<ScalarExpression> arguments,List<(ScalarType Type,SourceLocation Location)>? explicitArguments)
            =>GenericOf(method.Declaration) is { } generic?BindGenericArguments(new(method.Name,method.Declaration.Location),method.Declaration,generic,explicitArguments,arguments.ToList(),_structs[method.Owner]):_structs[method.Owner].TypeArguments;
        private bool ApplicableDeconstruct(ScalarStructMethod method,IReadOnlyList<ScalarExpression> arguments,List<(ScalarType Type,SourceLocation Location)>? explicitArguments=null)
        {
            if(method.Declaration.Parameters.Count!=arguments.Count)return false;
            IReadOnlyDictionary<string,ScalarType>? map;
            try{map=DeconstructArguments(method,arguments,explicitArguments);}catch(ScalarError){return false;}
            for(var i=0;i<arguments.Count;i++)
            {
                var argument=arguments[i];var parameter=method.Declaration.Parameters[i];var mode=ParameterPassing(parameter.Type).Mode;
                var type=MemberType(map,parameter.Type,argument.Location);
                if(ReferenceArgument(argument))
                {
                    if(mode==ScalarParameterMode.Value||argument.Operation!=(mode==ScalarParameterMode.Ref?"argument-ref":mode==ScalarParameterMode.Out?"argument-out":"argument-in"))return false;
                    if(argument.Type!=type&&!(argument.Operation=="argument-out"&&argument.Type==ScalarType.Unit))return false;
                }
                else if(mode is ScalarParameterMode.Ref or ScalarParameterMode.Out||TryCoerce(argument,type)is null)return false;
            }
            return true;
        }
        private sealed record FormalType(string Name,IReadOnlyList<FormalType> Arguments,bool Parameter);
        private FormalType DeconstructFormal(ScalarStructMethod method,int parameter)
        {
            var owner=_structs[method.Owner];var declaration=method;
            if(owner.Template>=0)
            {
                var suffix=method.QualifiedName[(owner.Name.Length+1)..];owner=_structs[owner.Template];
                declaration=owner.Methods.First(m=>m.Name==method.Name&&m.QualifiedName[(owner.Name.Length+1)..]==suffix);
            }
            var names=owner.TypeParameters.Concat(GenericOf(declaration.Declaration)?.Parameters.Select(p=>p.Name)??[]).ToHashSet(StringComparer.Ordinal);
            var text=ParameterPassing(declaration.Declaration.Parameters[parameter].Type).Element;
            var tokens=System.Text.RegularExpressions.Regex.Matches(text,@"[A-Za-z_][A-Za-z0-9_]*|<|>|,|\[|\]").Select(m=>m.Value).ToArray();var position=0;
            FormalType Read()
            {
                if(position>=tokens.Length)return new("?",[],false);
                var name=tokens[position++];var arguments=new List<FormalType>();
                if(position<tokens.Length&&tokens[position]=="<")
                {
                    position++;do{arguments.Add(Read());if(position>=tokens.Length||tokens[position]!=",")break;position++;}while(true);
                    if(position<tokens.Length&&tokens[position]==">")position++;
                }
                FormalType result=new(name,arguments,names.Contains(name)&&arguments.Count==0);
                while(position+1<tokens.Length&&tokens[position]=="["&&tokens[position+1]=="]"){position+=2;result=new("[]",[result],false);}
                return result;
            }
            return Read();
        }
        private bool BetterDeconstruct(ScalarStructMethod left,ScalarStructMethod right,IReadOnlyList<ScalarExpression> arguments,List<(ScalarType Type,SourceLocation Location)>? explicitArguments=null)
        {
            var leftMap=DeconstructArguments(left,arguments,explicitArguments);var rightMap=DeconstructArguments(right,arguments,explicitArguments);
            var better=false;var worse=false;
            void Formal(FormalType a,FormalType b)
            {
                if(a.Parameter!=b.Parameter){if(a.Parameter)worse=true;else better=true;return;}
                if(a.Name==b.Name&&a.Arguments.Count==b.Arguments.Count)for(var i=0;i<a.Arguments.Count;i++)Formal(a.Arguments[i],b.Arguments[i]);
            }
            for(var i=0;i<arguments.Count;i++)
            {
                var lp=left.Declaration.Parameters[i];var rp=right.Declaration.Parameters[i];
                var lt=MemberType(leftMap,lp.Type,arguments[i].Location);var rt=MemberType(rightMap,rp.Type,arguments[i].Location);
                if(lt==rt)
                {
                    var lm=ParameterPassing(lp.Type).Mode;var rm=ParameterPassing(rp.Type).Mode;
                    if(lm!=rm&&!ReferenceArgument(arguments[i])){if(lm==ScalarParameterMode.Value)better=true;else if(rm==ScalarParameterMode.Value)worse=true;}
                }
                else if(ReferenceArgument(arguments[i])&&arguments[i].Type==ScalarType.Unit)continue;
                else if(arguments[i].Type==lt)better=true;
                else if(arguments[i].Type==rt)worse=true;
                else
                {
                    var ltr=TryCoerce(new("local",lt,arguments[i].Location,"$overload"),rt)is not null;
                    var rtl=TryCoerce(new("local",rt,arguments[i].Location,"$overload"),lt)is not null;
                    if(ltr&&!rtl)better=true;else if(rtl&&!ltr)worse=true;
                }
            }
            if(better||worse)return better&&!worse;
            var leftGeneric=GenericOf(left.Declaration)is not null;var rightGeneric=GenericOf(right.Declaration)is not null;
            if(leftGeneric!=rightGeneric)return !leftGeneric;
            for(var i=0;i<arguments.Count;i++)
                if(MemberType(leftMap,left.Declaration.Parameters[i].Type,arguments[i].Location)==MemberType(rightMap,right.Declaration.Parameters[i].Type,arguments[i].Location))
                    Formal(DeconstructFormal(left,i),DeconstructFormal(right,i));
            return better&&!worse;
        }
        private (int Function,string Scope,int Call) DeconstructBindingSite(SourceLocation at)
        {
            var scope=_function?.Name??"<declaration>";
            if(_function?.Name=="__record_pattern"&&_struct is not null&&_qualifiedName?.StartsWith(_struct.Name+".",StringComparison.Ordinal)==true)scope=_qualifiedName[(_struct.Name.Length+1)..];
            return(_function?.Location.Offset??_declarationStart.Offset,scope,at.Offset);
        }
        private ScalarStructMethod RememberDeconstruct(ScalarStructMethod method,SourceLocation at)
        {
            if(_checkOnly)
            {
                var owner=_structs[method.Owner];var template=owner.Template>=0?owner.Template:method.Owner;
                checkedDeconstructSelections![DeconstructBindingSite(at)]=new(template,method.QualifiedName[(owner.Name.Length+1)..]);
            }
            return method;
        }
        private ScalarStructMethod SelectDeconstructCall(ScalarExpression receiver,IReadOnlyList<ScalarExpression> arguments,List<(ScalarType Type,SourceLocation Location)>? explicitArguments,SourceLocation at,string name="Deconstruct")
        {
            var owner=StructOf(receiver.Type);
            if(!_checkOnly&&(_typeArgs is not null||_struct?.TypeArguments is not null)&&checkedDeconstructSelections!.TryGetValue(DeconstructBindingSite(at),out var selected))
            {
                ScalarStructMethod? rebound=null;
                for(var k=IndexOf(owner.Id);k>=0&&rebound is null;k=_structs[k].BaseIndex)
                    if((_structs[k].Template>=0?_structs[k].Template:k)==selected.Template)rebound=_structs[k].Methods.FirstOrDefault(m=>m.Name==name&&m.QualifiedName[(_structs[k].Name.Length+1)..]==selected.Suffix);
                if(rebound is null||!ApplicableDeconstruct(rebound,arguments,explicitArguments))Fail("AI3107",$"The closed {name} call does not preserve its declaration binding.",at);
                return rebound!;
            }
            var all=DeconstructMethods(owner,name);
            var matching=all.Where(m=>explicitArguments is null||GenericOf(m.Declaration)?.Parameters.Count==explicitArguments.Count).Where(m=>ApplicableDeconstruct(m,arguments,explicitArguments)).ToList();
            var candidates=matching.Where(m=>m.IsStatic==(receiver.Operation=="static-type")&&AccessibleDeconstruct(m)).ToList();
            candidates=MostDerivedDeconstruct(owner,candidates);
            if(candidates.Count==0)
            {
                var hidden=matching.FirstOrDefault(m=>m.IsStatic==(receiver.Operation=="static-type"));
                if(hidden is not null)CheckAccess(_structs[hidden.Owner],hidden.IsPublic,name,at);
                if(matching.Count>0)Fail("AI3106",matching[0].IsStatic?"A static method must be accessed through its type.":"An instance method requires an object receiver.",at);
                Fail("AI3116",$"No {name} overload of '{owner.Name}' accepts these arguments.",at);
            }
            if(candidates.Count>1)candidates=candidates.Where(c=>!candidates.Any(other=>!ReferenceEquals(c,other)&&BetterDeconstruct(other,c,arguments,explicitArguments))).ToList();
            if(candidates.Count!=1)Fail("AI3106",$"The {name} call on '{owner.Name}' is ambiguous.",at);
            return RememberDeconstruct(candidates[0],at);
        }
        private ScalarStructMethod SelectDeconstructGroup(ScalarExpression site,IReadOnlyList<(string Name,string Type)> parameters,IReadOnlyList<ScalarType> types)
        {
            var values=parameters.Select((p,i)=>
            {
                var read=new ScalarExpression("local",types[i],site.Location,"$delegate_argument_"+i);var mode=ParameterPassing(p.Type).Mode;
                return mode==ScalarParameterMode.Value?read:new(mode==ScalarParameterMode.Out?"argument-out":mode==ScalarParameterMode.Ref?"argument-ref":"argument-in",types[i],site.Location,Left:read);
            }).ToArray();
            return SelectDeconstructCall(site.Left!,values,site.Arguments?.Select(a=>(a.Type,a.Location)).ToList(),site.Location,site.Value!);
        }
        private int PositionalPatternArity()
        {
            if(Current.Text==")")return 0;var count=1;var depth=0;var angles=0;
            for(var i=_position;i<_tokens.Count;i++)
            {
                var text=_tokens[i].Text;if(text==""||text==")"&&depth==0&&angles==0)break;
                if(text is "(" or "[" or "{")depth++;
                else if(text is ")" or "]" or "}")depth--;
                else if(text=="<"&&i>_position&&StructNamedAny(_tokens[i-1].Text)is {TypeParameters.Count:>0})angles++;
                else if(angles>0&&text is ">" or ">>" or ">>>")angles=System.Math.Max(0,angles-text.Length);
                else if(text==","&&depth==0&&angles==0)count++;
            }
            return count;
        }
        private ScalarStructMethod SelectPatternDeconstruct(ScalarStruct owner,int count,SourceLocation at)
        {
            var all=DeconstructMethods(owner);
            var candidates=all.Where(m=>!m.IsStatic&&GenericOf(m.Declaration)is null&&AccessibleDeconstruct(m)&&m.Declaration.Parameters.Count==count&&m.Declaration.Parameters.All(p=>ParameterPassing(p.Type).Mode==ScalarParameterMode.Out)).ToList();
            if(candidates.Count==0&&owner.IsRecord&&owner.Positional>0)
                candidates=all.Where(m=>m.QualifiedName.EndsWith(".$deconstruct_primary",StringComparison.Ordinal)||m.Name=="Deconstruct"&&m.Declaration.Parameters.Count==owner.Positional&&m.Declaration.Parameters.Select((p,i)=>ParameterPassing(p.Type).Mode==ScalarParameterMode.Out&&DeconstructType(owner,p.Type)==owner.RecordParameterTypes[i]).All(v=>v)).ToList();
            if(candidates.Count==0)Fail("AI3106",$"'{owner.Name}' has no accessible instance Deconstruct with {count} out parameter(s).",at);
            candidates=MostDerivedDeconstruct(owner,candidates);
            if(candidates.Count>1)
            {
                var unknown=candidates[0].Declaration.Parameters.Select(p=>new ScalarExpression("argument-out",ScalarType.Unit,at,Left:new("local",ScalarType.Unit,at,"$pattern_argument"))).ToArray();
                candidates=candidates.Where(c=>!candidates.Any(other=>!ReferenceEquals(c,other)&&BetterDeconstruct(other,c,unknown))).ToList();
            }
            if(candidates.Count!=1)Fail("AI3106",$"The positional Deconstruct on '{owner.Name}' is ambiguous.",at);
            var method=candidates[0];if(method.Declaration.ReturnType is not ("unit" or "void"))Fail("AI3106","A positional Deconstruct method must return unit.",at);
            return method;
        }
    }
}
