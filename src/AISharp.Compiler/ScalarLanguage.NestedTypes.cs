namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static bool CompilationNestedWithin(string inner,string outer)
    {
        for(var binding=CompilationBinding(inner);binding is not null;binding=binding.DeclaringType is {} parent?CompilationBinding(parent):null)
            if(binding.InternalName==outer.Split('<')[0])return true;
        return false;
    }
    private static void ValidateCompilationNestedMembers()
    {
        if(activeCompilation is null)return;
        foreach(var nested in activeCompilation.Bindings.Where(b=>b.DeclaringType is not null))
        {
            var parent=activeStructs!.FirstOrDefault(s=>s.Name==nested.DeclaringType);if(parent is null)continue;var index=IndexOf(parent.Id);
            if(parent.Fields.Any(f=>f.Owner==index&&f.Name==nested.Name)||parent.StaticFields.Any(f=>f.Owner==index&&f.Name==nested.Name)
                ||parent.Properties.Any(p=>p.Owner==index&&p.Name==nested.Name)||parent.Methods.Any(m=>m.Owner==index&&m.Name==nested.Name&&!m.IsAccessor))
                throw DeclarationError("AI3106","A nested type conflicts with a member named '"+nested.Name+"'.",activeStructs!.FirstOrDefault(s=>s.Name==nested.InternalName)?.Location??parent.Location);
        }
        ValidateCompilationNestedExposure();
    }
    private sealed record TypeAccessDomain(string Owner,bool Family);
    private static void ValidateCompilationNestedExposure()
    {
        if(activeCompilation is null||!activeCompilation.Bindings.Any(b=>b.DeclaringType is not null&&b.Accessibility is "private" or "protected" or "private protected"))return;
        List<TypeAccessDomain> Domain(string name)
        {
            var result=new List<TypeAccessDomain>();
            for(var binding=CompilationBinding(name);binding?.DeclaringType is {} parent;binding=CompilationBinding(parent))
            {
                if(binding.Accessibility=="private")result.Add(new(parent,false));
                else if(binding.Accessibility is "protected" or "private protected")result.Add(new(parent,true));
            }
            return result;
        }
        bool Related(string actor,string owner,bool family)
        {
            if(CompilationNestedWithin(actor,owner))return true;if(!family)return false;
            var target=activeStructs!.FirstOrDefault(s=>s.Name==owner);if(target is null)return false;
            for(var context=CompilationBinding(actor);context is not null;context=context.DeclaringType is {} p?CompilationBinding(p):null)
                if(activeStructs!.FirstOrDefault(s=>s.Name==context.InternalName) is {} current&&Derives(activeStructs!,IndexOf(current.Id),IndexOf(target.Id)))return true;
            return false;
        }
        void Check(ScalarType type,List<TypeAccessDomain> memberDomain,SourceLocation at,HashSet<ScalarType> visited)
        {
            if(!visited.Add(type))return;
            var owner=(int)type>=StructTypeBase&&(int)type<StructTypeBase+activeStructs!.Count?activeStructs[(int)type-StructTypeBase]:null;
            var enumeration=(int)type>=EnumTypeBase&&(int)type<EnumTypeBase+activeEnums!.Count?activeEnums[(int)type-EnumTypeBase]:null;
            foreach(var required in Domain(owner?.Name??enumeration?.Name??""))
                if(!memberDomain.Any(d=>(required.Family||!d.Family)&&Related(d.Owner,required.Owner,required.Family)))
                    throw DeclarationError("AI3119","A member exposes a nested type outside its accessibility domain.",at);
            if(owner?.ArrayElementType is {} element)Check(element,memberDomain,at,visited);
            if(owner is not null)foreach(var argument in owner.TypeArgumentList)Check(argument,memberDomain,at,visited);
            if(enumeration is not null)
            {
                void Argument(ScalarRuntimeTypeShape shape)
                {
                    if(shape.NamedType is {} named)Check(named,memberDomain,at,visited);
                    if(shape.Element is {} nested)Argument(nested);
                    if(shape.Arguments is {} arguments)foreach(var child in arguments)Argument(child);
                }
                foreach(var argument in enumeration.TypeArguments)Argument(argument);
            }
        }
        foreach(var owner in activeStructs!.ToArray())
        {
            if(CompilationBinding(owner.Name) is null||owner.IsTypeParameter)continue;var index=IndexOf(owner.Id);
            List<TypeAccessDomain> MemberDomain(bool visible,bool assembly=false,bool family=false)
            {var result=Domain(owner.Name);if(!visible&&!assembly)result.Add(new(owner.Name.Split('<')[0],family));return result;}
            foreach(var field in owner.Fields.Concat(owner.StaticFields).Where(f=>f.Owner==index&&!f.Hidden))Check(field.Type,MemberDomain(field.IsPublic,field.IsInternal),field.Location,[]);
            foreach(var property in owner.Properties.Where(p=>p.Owner==index&&!p.IsSynthesizedRecordTypeContract))Check(property.Type,MemberDomain(property.IsPublic,property.IsInternal,property.IsProtectedMember),property.Location,[]);
            var binder=new ExtendedBinder(activeFeatures,"",owner.Location);
            foreach(var method in owner.Methods.Where(m=>m.Owner==index&&!m.IsAccessor&&!m.IsLifted))
            {
                var arguments=owner.TypeArguments is null?new Dictionary<string,ScalarType>():new Dictionary<string,ScalarType>(owner.TypeArguments);
                if(ExtendedBinder.GenericOf(method.Declaration) is {} generic)foreach(var parameter in generic.Parameters)if(parameter.Placeholder>=0)arguments[parameter.Name]=(ScalarType)(StructTypeBase+parameter.Placeholder);
                var domain=MemberDomain(method.IsPublic,method.IsInternal,method.IsProtectedMember);
                Check(binder.CompilationMemberType(arguments,method.Declaration.ReturnType,method.Declaration.Location,true),domain,method.Declaration.Location,[]);
                foreach(var parameter in method.Declaration.Parameters)Check(binder.CompilationMemberType(arguments,parameter.Type,method.Declaration.Location),domain,method.Declaration.Location,[]);
            }
        }
    }
    private sealed partial class ExtendedBinder
    {
        public ScalarType CompilationMemberType(IReadOnlyDictionary<string,ScalarType> arguments,string syntax,SourceLocation at,bool allowUnit=false)=>MemberType(arguments,syntax,at,allowUnit);
        private string? CompilationInheritedTypeText(string text,SourceLocation at)
        {
            if(activeCompilation is null)return null;var head=text.Split('<','[')[0];if(!IdentifierSpelling.Valid(head))return null;
            var name=IdentifierName(head);var actor=activeCompilation.TypeOwnerResolver?.Invoke(at.Offset)??_struct?.Name;
            var owner=actor is null?null:_structs.FirstOrDefault(s=>s.Name==actor);if(owner is null)return null;
            var arity=text.Length>head.Length&&text[head.Length]=='<'&&text.EndsWith('>')?SplitTypeList(text[(head.Length+1)..^1]).Count():0;
            for(var index=IndexOf(owner.Id);index>=0;index=_structs[index].BaseIndex)
            {
                var parent=_structs[index];var nested=activeCompilation.Bindings.FirstOrDefault(b=>b.DeclaringType==parent.Name&&b.Name==name&&b.Arity==arity);
                if(nested is null||nested.CapturedArity>0||nested.Accessibility=="private"&&!CompilationNestedWithin(actor!,parent.Name))continue;
                return nested.InternalName+text[head.Length..];
            }
            return null;
        }
        private void CompilationTypeAccess(ScalarType type,SourceLocation at)
        {
            if(activeCompilation is null||activeCompilation.IsGeneratedOffset(at.Offset))return;
            var binding=CompilationBinding(IsEnum(type)?EnumOf(type).Name:IsStruct(type)?StructOf(type).Name:"");
            var actor=activeCompilation.TypeOwnerResolver?.Invoke(at.Offset)??_struct?.Name;
            for(;binding?.DeclaringType is {} ownerName;binding=CompilationBinding(ownerName))
            {
                var allowed=binding.Accessibility is "public" or "internal" or "protected internal";
                if(actor is not null&&CompilationNestedWithin(actor,ownerName))allowed=true;
                if(!allowed&&binding.Accessibility is "protected" or "private protected")
                {
                    var owner=_structs.FirstOrDefault(s=>s.Name==ownerName);
                    for(var context=actor is null?null:CompilationBinding(actor);context is not null;context=context.DeclaringType is {} p?CompilationBinding(p):null)
                        if(owner is not null&&_structs.FirstOrDefault(s=>s.Name==context.InternalName) is {} current&&Derives(_structs,IndexOf(current.Id),IndexOf(owner.Id)))allowed=true;
                }
                if(!allowed)Fail("AI3119","Nested type '"+binding.FullName+"' is inaccessible due to its protection level.",at);
            }
        }
    }
    private sealed partial class CompilationBinder
    {
        public static bool ContainsNestedTypes(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if(!TypeWords.Any(w=>source.Contains(w,StringComparison.Ordinal)))return false;
            var tokens=CompilationTokens(source);var depth=0;var openings=new HashSet<int>();var bodies=new Stack<int>();
            int NextAt(int at){while(at<tokens.Count-1&&tokens[at].Kind==TokenKind.NewLine)at++;return at;}
            for(var i=0;i<tokens.Count-1;i++)
            {
                var token=tokens[i];if(token.Kind==TokenKind.String)continue;
                var previous=i-1;while(previous>=0&&tokens[previous].Kind==TokenKind.NewLine)previous--;
                if(token.Text is "class" or "struct"&&previous>=0&&tokens[previous].Text is ":" or ",")continue;
                if((depth==0||bodies.Count>0&&depth==bodies.Peek())&&TypeWords.Contains(token.Text))
                {
                    var name=NextAt(i+1);
                    if(token.Text=="record"&&tokens[name].Text is "class" or "struct"||token.Text=="delegate"&&tokens[name].Text=="fn")name=NextAt(name+1);
                    if(DeclarationIdentifier(tokens[name]))
                    {
                        if(bodies.Count>0)return true;
                        var parens=0;
                        for(var p=name+1;p<tokens.Count-1;p++)
                        {
                            if(tokens[p].Kind==TokenKind.String)continue;
                            if(tokens[p].Text=="(")parens++;else if(tokens[p].Text==")")parens--;
                            if(parens==0&&tokens[p].Text=="{"){openings.Add(p);break;}
                            if(parens==0&&(tokens[p].Text==";"||token.Text is "record" or "delegate"&&tokens[p].Kind==TokenKind.NewLine))break;
                        }
                    }
                }
                if(token.Text=="{"){depth++;if(openings.Contains(i))bodies.Push(depth);}
                else if(token.Text=="}"){if(bodies.Count>0&&bodies.Peek()==depth)bodies.Pop();depth--;}
            }
            return false;
        }
        private void ReadNestedTypes(Unit unit,Symbol parent,int open,int close)
        {
            var t=unit.Tokens;var bodyScope=new Scope(unit,parent.QualifiedName,Offset(unit,open)+1,Offset(unit,close),parent.Scope){TypeOwner=parent};unit.Scopes.Add(bodyScope);
            for(var i=open+1;i<close;)
            {
                i=Next(unit,i);if(i>=close)break;var start=i;var declaration=i;var modifiers=new List<int>();
                while(declaration<close&&(Modifiers.Contains(t[declaration].Text)||t[declaration].Text is "new" or "virtual" or "override" or "required")&&t[Next(unit,declaration+1)].Text!=":"){modifiers.Add(declaration);declaration=Next(unit,declaration+1);}
                var kind=t[declaration].Text;var nameIndex=Next(unit,declaration+1);
                if(kind=="record"&&t[nameIndex].Text is "class" or "struct"||kind=="delegate"&&t[nameIndex].Text=="fn")nameIndex=Next(unit,nameIndex+1);
                if(TypeWords.Contains(kind)&&SourceIdentifier(t[nameIndex]))
                {
                    if(parent.Kind=="enum")Fail(unit,Offset(unit,nameIndex),"An enum body cannot declare nested types.");
                    result.RequiredFeatures|=ScalarFeatures.NestedTypes;
                    if(!DeclarationIdentifier(t[nameIndex]))Fail(unit,Offset(unit,nameIndex),"Invalid nested declaration name.");
                    var name=Name(t[nameIndex].Text);var cursor=Next(unit,nameIndex+1);var arity=0;
                    if(t[cursor].Text=="<"){var end=Match(unit,cursor,"<",">");arity=1+t.Skip(cursor+1).Take(end-cursor-1).Count(x=>x.Text==",");cursor=Next(unit,end+1);}
                    var stop=cursor;
                    while(stop<close&&(t[stop].Kind==TokenKind.String||t[stop].Text is not ("{" or ";")))
                    {if(kind is "record" or "delegate"&&t[stop].Kind==TokenKind.NewLine)break;stop++;}
                    var nestedOpen=-1;var nestedClose=-1;int endOffset;
                    if(stop<close&&t[stop].Text=="{"){nestedOpen=stop;nestedClose=Match(unit,stop,"{","}");endOffset=Offset(unit,nestedClose)+1;cursor=nestedClose+1;}
                    else if(kind is "record" or "delegate"){endOffset=Offset(unit,stop)+(t[stop].Text==";"?1:0);cursor=stop+1;}
                    else {Fail(unit,Offset(unit,start),"A nested type requires a body.");return;}
                    var declarationModifiers=modifiers.Select(x=>t[x].Text).ToArray();var isPartial=declarationModifiers.Contains("partial");
                    if(declarationModifiers.Contains("file"))Fail(unit,Offset(unit,nameIndex),"The file modifier is only valid on a top-level type.");
                    if(isPartial&&kind is "enum" or "delegate")Fail(unit,Offset(unit,nameIndex),"This declaration kind cannot be partial.");
                    var previousDeclaration=symbols.FirstOrDefault(s=>!s.Function&&s.Owner?.InternalName==parent.InternalName&&s.Name==name&&s.Arity==arity);
                    if(hasFileDeclarations)previousDeclaration=null;
                    if(previousDeclaration is not null&&(!isPartial||!previousDeclaration.Partial))Fail(unit,Offset(unit,nameIndex),"Duplicate nested type '"+name+"'."+(isPartial||previousDeclaration.Partial?" Every type part must be partial.":""));
                    var access=modifiers.Where(x=>t[x].Text is "public" or "private" or "protected" or "internal").Select(x=>t[x].Text).ToArray();
                    var visibility=access.Length==0?parent.Kind=="interface"?"public":"private":string.Join(" ",access);
                    if(access.Length>1&&visibility is not ("protected internal" or "internal protected" or "private protected" or "protected private"))Fail(unit,Offset(unit,start),"Invalid nested type accessibility modifiers.");
                    if(visibility=="internal protected")visibility="protected internal";if(visibility=="protected private")visibility="private protected";
                    foreach(var modifier in modifiers.Where(x=>t[x].Text is "public" or "private" or "protected" or "internal" or "new"))Mask(unit,Offset(unit,modifier),Offset(unit,modifier)+t[modifier].Text.Length);
                    var nested=new Symbol(unit,bodyScope,name,arity,false,Offset(unit,nameIndex),Offset(unit,start),endOffset,previousDeclaration?.InternalName??"__unit_t_"+symbols.Count){Owner=parent,Accessibility=visibility,Kind=kind,Partial=isPartial,DeclarationModifiers=declarationModifiers};symbols.Add(nested);
                    if(isPartial)result.RequiredFeatures|=ScalarFeatures.PartialTypes;
                    if(nestedOpen>=0)ReadNestedTypes(unit,nested,nestedOpen,nestedClose);i=cursor;continue;
                }
                if(kind=="fn"&&Name(t[nameIndex].Text)==parent.Name&&t[nameIndex].Text!="new"
                    ||Identifier(t[declaration])&&Name(t[declaration].Text)==parent.Name&&t[Next(unit,declaration+1)].Text==":")
                    Fail(unit,Offset(unit,kind=="fn"?nameIndex:declaration),"A source member cannot have the same name as its containing type.");
                // Skip an ordinary field, property or method as a whole, including its body.
                var p=i;var parens=0;var brackets=0;
                while(p<close)
                {
                    if(t[p].Kind==TokenKind.String){p++;continue;}
                    if(t[p].Text=="(")parens++;else if(t[p].Text==")")parens--;else if(t[p].Text=="[")brackets++;else if(t[p].Text=="]")brackets--;
                    if(parens==0&&brackets==0&&t[p].Text=="{"){p=Match(unit,p,"{","}")+1;break;}
                    if(parens==0&&brackets==0&&(t[p].Kind==TokenKind.NewLine||t[p].Text==";")){p++;break;}p++;
                }
                i=Math.Max(i+1,p);
            }
        }
    }
}
