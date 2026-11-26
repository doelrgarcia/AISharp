namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static ScalarStruct CompilationMemberMetadata(ScalarStruct owner)
    {
        var compilation=activeCompilation;if(compilation is null)return owner;
        IReadOnlyList<string> Names(IReadOnlyList<ParameterDeclaration> parameters,int offset)
        {
            var defined=compilation.PartialParameterResolver?.Invoke(offset);
            return Array.AsReadOnly(parameters.Select((p,i)=>defined is not null&&i<defined.Count?defined[i]:IdentifierName(p.Name)).ToArray());
        }
        return owner with
        {
            Methods=owner.Methods.Select(m=>m with
            {
                SourceParameterNames=Names(m.Declaration.Parameters,m.Declaration.Location.Offset),
                SourceTypeParameterNames=Array.AsReadOnly((compilation.PartialTypeParameterResolver?.Invoke(m.Declaration.Location.Offset)??ExtendedBinder.GenericOf(m.Declaration)?.Parameters.Select(p=>p.Name).ToArray()??[]).ToArray())
            }).ToArray(),
            Properties=owner.Properties.Select(p=>p with{SourceIndexParameterNames=Names(p.IndexParameters,p.Location.Offset)}).ToArray()
        };
    }
    private static bool OmittedPartial(FunctionDeclaration method)=>activeCompilation?.OmittedPartialResolver?.Invoke(method.Location.Offset)==true;
    private sealed partial class CompilationBinder
    {
        private sealed record PartialMember(Symbol Owner,int Start,int End,int NameOffset,string Name,string Kind,IReadOnlyList<string> Modifiers,IReadOnlyList<string> Generic,IReadOnlyList<(string Name,string Type)> Parameters,string Return,string Constraints,string Accessors,bool Definition,int Terminator);
        private readonly List<(PartialMember Body,PartialMember Definition,bool Omitted)> partialMembers=[];
        private static readonly HashSet<string> MemberModifiers=new(StringComparer.Ordinal){"public","private","protected","internal","static","readonly","virtual","override","abstract","sealed","required","async","new","partial"};
        private string PartialSignatureType(Unit unit,Symbol owner,IReadOnlyList<Token> tokens,ref int at,IReadOnlyList<string> generic)
        {
            at=Next(unit,at);var prefix="";
            while(tokens[at].Text is "scoped" or "ref" or "out" or "in" or "readonly"){prefix+=tokens[at].Text+" ";at=Next(unit,at+1);}
            var start=at;var type=TypeSyntax(unit,tokens,ref at,ScopeAt(unit,Offset(unit,start)),Offset(unit,start),true,true);
            if(start==at)Fail(unit,Offset(unit,start),"A partial member requires a bound signature type.");
            foreach(var builtin in QualifiedBuiltinTypes.OrderByDescending(p=>p.Key.Length))type=type.Replace("global::"+builtin.Key,builtin.Value,StringComparison.Ordinal);
            type=RewriteTypeIdentifiers(type,name=>generic.Select((n,i)=>(n,i)).FirstOrDefault(p=>Name(p.n)==name) is var found&&found.n is not null?"__method_parameter_"+found.i:null);
            return prefix+(type is "void"?"unit":type);
        }
        private PartialMember ReadPartialMember(Symbol owner,int partial,int start)
        {
            var unit=owner.Unit;var t=unit.Tokens;var at=start;var modifiers=new List<string>();
            while(t[at].Kind==TokenKind.NewLine||MemberModifiers.Contains(t[at].Text)){if(t[at].Kind!=TokenKind.NewLine){if(modifiers.Contains(t[at].Text))Fail(unit,Offset(unit,at),"Duplicate partial member modifier.");modifiers.Add(t[at].Text);}at++;}
            var kind=t[at].Text=="fn"?"method":t[at].Text=="event"?"event":"property";
            if(kind is "method" or "event")at=Next(unit,at+1);
            var name=t[at];if(!Identifier(name))Fail(unit,Offset(unit,at),"A partial member requires a name.");at=Next(unit,at+1);
            if(kind=="method"&&name.Text=="new")kind="constructor";
            if(kind=="property"&&name.Text=="this")kind="indexer";
            var generic=new List<string>();
            if(t[at].Text=="<")
            {
                if(kind!="method")Fail(unit,Offset(unit,at),"Only a partial method can declare type parameters.");
                var close=Match(unit,at,"<",">");at=Next(unit,at+1);
                while(at<close){if(!DeclarationIdentifier(t[at])||generic.Select(Name).Contains(Name(t[at].Text)))Fail(unit,Offset(unit,at),"Invalid partial method type parameter.");generic.Add(t[at].Text);at=Next(unit,at+1);if(at<close){if(t[at].Text!=",")Fail(unit,Offset(unit,at),"Expected a partial method type parameter separator.");at=Next(unit,at+1);if(at==close)Fail(unit,Offset(unit,at),"A partial method type parameter list cannot end with a separator.");}}
                at=Next(unit,close+1);
            }
            var parameters=new List<(string Name,string Type)>();
            if(kind is "method" or "constructor" or "indexer")
            {
                var open=kind=="indexer"?"[":"(";var end=kind=="indexer"?"]":")";
                if(t[at].Text!=open)Fail(unit,Offset(unit,at),"Expected partial member parameter list.");at=Next(unit,at+1);
                while(t[at].Text!=end)
                {
                    var parameter=t[at];if(!DeclarationIdentifier(parameter)||parameters.Any(p=>Name(p.Name)==Name(parameter.Text)))Fail(unit,Offset(unit,at),"Invalid or duplicate partial member parameter.");at=Next(unit,at+1);
                    if(t[at].Text!=":")Fail(unit,Offset(unit,at),"Expected ':' after partial member parameter.");at=Next(unit,at+1);
                    var type=PartialSignatureType(unit,owner,t,ref at,generic);parameters.Add((parameter.Text,type));at=Next(unit,at);
                    if(t[at].Text=="=")Fail(unit,Offset(unit,at),"Default arguments require the explicit optional-parameter capability.","AI3100");
                    if(t[at].Text==end)break;if(t[at].Text!=",")Fail(unit,Offset(unit,at),"Expected a partial member parameter separator.");at=Next(unit,at+1);
                }
                at=Next(unit,at+1);
            }
            if(kind is "method" or "constructor")
            {if(t[at].Text!="->")Fail(unit,Offset(unit,at),"A partial function requires its return type.");at=Next(unit,at+1);}
            else{if(t[at].Text!=":")Fail(unit,Offset(unit,at),"A partial property or event requires its type.");at=Next(unit,at+1);}
            var resultType=PartialSignatureType(unit,owner,t,ref at,generic);at=Next(unit,at);
            var constraints=new List<string>();
            while(t[at].Text=="where")
            {
                at=Next(unit,at+1);var parameter=generic.FindIndex(n=>Name(n)==Name(t[at].Text));if(parameter<0)Fail(unit,Offset(unit,at),"A partial method constraint must name one of its parameters.");at=Next(unit,at+1);
                if(t[at].Text!=":")Fail(unit,Offset(unit,at),"Expected ':' in partial method constraint.");at=Next(unit,at+1);var values=new List<string>();
                while(true)
                {
                    string value;
                    if(t[at].Text=="new"&&t[Next(unit,at+1)].Text=="(")
                    {var close=Next(unit,Next(unit,at+1)+1);if(t[close].Text!=")")Fail(unit,Offset(unit,close),"A constructor constraint requires new().");value="new()";at=Next(unit,close+1);}
                    else if(t[at].Text is "class" or "struct" or "unmanaged" or "notnull"){value=t[at].Text;at=Next(unit,at+1);}
                    else{value=PartialSignatureType(unit,owner,t,ref at,generic);at=Next(unit,at);}
                    values.Add(value);if(t[at].Text!=",")break;at=Next(unit,at+1);
                }
                constraints.Add(parameter+":"+string.Join(",",values));
            }
            var contracts=false;
            while(t[at].Text is not ("{" or ";" or "=" or "=>" or "}" or "")){contracts=true;at++;}
            var terminator=Offset(unit,at);var definition=t[at].Text==";";var accessors="";var stop=at;
            if(t[at].Text=="=>"&&kind is "property" or "indexer")
            {
                definition=false;accessors=":get";stop=Next(unit,at+1);
                while(stop<t.Count-1&&t[stop].Text!=";")
                {
                    if(t[stop].Kind!=TokenKind.String&&t[stop].Text is "(" or "[" or "{"){var open=t[stop].Text;stop=Match(unit,stop,open,open=="("?")":open=="["?"]":"}");}
                    stop++;
                }
                if(t[stop].Text!=";")Fail(unit,Offset(unit,at),"An expression-bodied partial accessor requires its terminator.");
            }
            else if(t[at].Text=="{")
            {
                stop=Match(unit,at,"{","}");
                if(kind is "property" or "indexer" or "event")
                {
                    definition=true;var members=new List<string>();var cursor=Next(unit,at+1);
                    while(cursor<stop)
                    {
                        var access=new List<string>();while(t[cursor].Text is "private" or "protected" or "internal" or "readonly"){access.Add(t[cursor].Text);cursor=Next(unit,cursor+1);}
                        var accessor=t[cursor];if(accessor.Text is not ("get" or "set" or "init" or "add" or "remove"))Fail(unit,Offset(unit,cursor),"Invalid partial accessor.");members.Add(string.Join(" ",access)+":"+accessor.Text);cursor=Next(unit,cursor+1);
                        if(t[cursor].Text==";")cursor=Next(unit,cursor+1);
                        else if(t[cursor].Text=="{"){definition=false;cursor=Next(unit,Match(unit,cursor,"{","}")+1);}
                        else Fail(unit,Offset(unit,cursor),"A partial accessor requires a semicolon or body.");
                    }
                    if(members.Distinct(StringComparer.Ordinal).Count()!=members.Count)Fail(unit,name.Location.Offset,"Duplicate partial accessor.");
                    accessors=string.Join(";",members.Order(StringComparer.Ordinal));
                }
            }
            else if(!definition)Fail(unit,Offset(unit,at),"A partial member requires a declaration or implementation body.");
            if(definition&&modifiers.Contains("async"))Fail(unit,name.Location.Offset,"The defining declaration of a partial method cannot be async.");
            if(definition&&contracts)Fail(unit,name.Location.Offset,"Contracts on a partial defining declaration require paired contract metadata.","AI3100");
            if(kind=="event"&&definition&&accessors.Length>0)Fail(unit,name.Location.Offset,"A partial event definition must be field-like.");
            return new(owner,Offset(unit,start),Offset(unit,stop)+1,name.Location.Offset,Name(name.Text),kind,modifiers,generic,parameters,resultType,string.Join(";",constraints.Order(StringComparer.Ordinal)),accessors,definition,terminator);
        }
        private void RemovePartialModifier(PartialMember member)
        {
            foreach(var token in member.Owner.Unit.Tokens.Where(t=>t.Location.Offset>=member.Start&&t.Location.Offset<member.NameOffset&&t.Text=="partial"))
                if(member.Owner.Kind=="interface"&&member.Kind=="method"&&!member.Modifiers.Any(m=>m is "public" or "private" or "protected" or "internal"))Replace(member.Owner.Unit,token.Location.Offset,token.Location.Offset+token.Text.Length,"private");
                else Mask(member.Owner.Unit,token.Location.Offset,token.Location.Offset+token.Text.Length);
        }
        private void ValidatePartialMembers()
        {
            var declarations=new List<PartialMember>();
            foreach(var owner in symbols.Where(s=>!s.Function))
            {
                var unit=owner.Unit;var t=unit.Tokens;var at=t.ToList().FindIndex(token=>token.Location.Offset>owner.Offset&&token.Text=="{");if(at<0||Offset(unit,at)>=owner.End)continue;
                for(at++;at<t.Count&&Offset(unit,at)<owner.End-1;at++)
                {
                    if(t[at].Kind==TokenKind.String)continue;
                    if(t[at].Text=="{"){at=Match(unit,at,"{","}");continue;}
                    if(t[at].Text!="partial")continue;
                    var following=Next(unit,at+1);while(MemberModifiers.Contains(t[following].Text))following=Next(unit,following+1);
                    if(TypeWords.Contains(t[following].Text))continue;
                    var afterName=Next(unit,following+1);
                    if(t[following].Text is not ("fn" or "event")&&!(Identifier(t[following])&&(t[afterName].Text==":"||t[following].Text=="this"&&t[afterName].Text=="[")))continue;
                    if(!owner.Partial)Fail(unit,Offset(unit,at),"A partial member must belong to a partial type declaration.");
                    var start=at;while(start>0&&(t[start-1].Kind==TokenKind.NewLine||MemberModifiers.Contains(t[start-1].Text)))start--;
                    var member=ReadPartialMember(owner,at,start);declarations.Add(member);result.RequiredFeatures|=ScalarFeatures.PartialMembers;
                    while(at<t.Count&&Offset(unit,at)<member.End)at++;at--;
                }
            }
            foreach(var group in declarations.GroupBy(m=>m.Owner.InternalName+"/"+m.Kind+"/"+m.Name+"/"+m.Generic.Count+"/"+string.Join(";",m.Parameters.Select(p=>p.Type))))
            {
                var definitions=group.Where(m=>m.Definition).ToArray();var bodies=group.Where(m=>!m.Definition).ToArray();var first=group.First();
                if(definitions.Length!=1||bodies.Length>1)Fail(first.Owner.Unit,first.NameOffset,"A partial member requires exactly one defining declaration and at most one implementation.");
                var definition=definitions[0];
                if(bodies.Length==0)
                {
                    if(definition.Kind!="method"||definition.Return!="unit"||definition.Modifiers.Any(m=>m is "public" or "private" or "protected" or "internal" or "virtual" or "override" or "sealed" or "abstract")||definition.Parameters.Any(p=>p.Type.StartsWith("out ",StringComparison.Ordinal)))Fail(definition.Owner.Unit,definition.NameOffset,"This partial member requires an implementation.");
                    partialMembers.Add((definition,definition,true));
                    Replace(definition.Owner.Unit,definition.Terminator,definition.Terminator+1,"\n ensures true\n{}\n");
                    RemovePartialModifier(definition);
                    continue;
                }
                var body=bodies[0];
                string Mods(PartialMember member)=>string.Join(" ",member.Modifiers.Where(m=>m is not ("partial" or "async")).Order(StringComparer.Ordinal));
                if(definition.Return!=body.Return||Mods(definition)!=Mods(body)||definition.Constraints!=body.Constraints||definition.Kind is "property" or "indexer"&&definition.Accessors!=body.Accessors)Fail(body.Owner.Unit,body.NameOffset,"Partial member signatures, modifiers, constraints and accessor contracts must agree.");
                partialMembers.Add((body,definition,false));Mask(definition.Owner.Unit,definition.Start,definition.End);
                RemovePartialModifier(body);
            }
        }
    }
}
