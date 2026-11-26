namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class CompilationBinder
    {
        private sealed record Capture(Symbol Owner,int Index,string Original,string Flat);
        private static string FlatParameter(Symbol owner,int index)=>"__unit_capture_"+owner.InternalName["__unit_t_".Length..]+"_"+index;
        private IReadOnlyList<string> Parameters(Symbol symbol)
        {
            if(symbol.Arity==0)return [];
            var unit=symbol.Unit;var tokens=unit.Tokens;var name=tokens.ToList().FindIndex(t=>t.Location.Offset==symbol.Offset);var start=Next(unit,name+1);
            if(tokens[start].Text!="<")return [];var end=Match(unit,start,"<",">");
            return tokens.Skip(start+1).Take(end-start-1).Where(t=>Identifier(t)&&t.Text is not ("in" or "out")).Select(t=>t.Text).ToArray();
        }
        private List<Capture> Captures(Symbol? symbol)
        {
            if(symbol?.Owner is not {} owner)return [];
            var result=Captures(owner);var parameters=Parameters(owner);
            for(var i=0;i<parameters.Count;i++)result.Add(new(owner,i,parameters[i],FlatParameter(owner,i)));
            return result;
        }
        private string? ParameterReplacement(Unit unit,int at,string name)=>unit.Shadows.Select((s,i)=>(s,i)).Where(x=>x.s.Type&&x.s.Name==name&&x.s.Start<=at&&at<x.s.End).OrderBy(x=>x.s.End-x.s.Start).ThenByDescending(x=>x.i).FirstOrDefault().s?.Replacement;
        private bool RewriteCapturedDeclaration(Unit unit,Symbol symbol)
        {
            var captures=Captures(symbol);if(captures.Count==0)return false;
            if(symbol.Kind=="enum"&&symbol.Arity!=0)Fail(unit,symbol.Offset,"An enum cannot declare its own type parameters.");
            var tokens=unit.Tokens;var name=tokens.ToList().FindIndex(t=>t.Location.Offset==symbol.Offset);var end=name+1;var own="";
            if(tokens[Next(unit,end)].Text=="<")
            {
                var open=Next(unit,end);var close=Match(unit,open,"<",">");own=TokenText(unit,open+1,close);end=close+1;
            }
            var parameters=string.Join(",",captures.Select(c=>c.Flat))+(own.Length==0?"":","+own);
            Replace(unit,symbol.Offset,Offset(unit,end-1)+tokens[end-1].Text.Length,Internal(symbol)+"<"+parameters+">");
            var body=end;while(body<tokens.Count-1&&tokens[body].Text!="{"&&Offset(unit,body)<symbol.End)body++;
            var insertion=body<tokens.Count-1&&tokens[body].Text=="{"?Offset(unit,body):symbol.End;
            if(insertion==symbol.End&&tokens.LastOrDefault(t=>t.Location.Offset>=symbol.Offset&&t.Location.Offset<symbol.End&&t.Kind!=TokenKind.NewLine) is {} terminator&&terminator.Text==";")insertion=terminator.Location.Offset;
            var clauses=new List<string>();
            foreach(var capture in captures)
            {
                var parent=capture.Owner;
                if(partialTypes.TryGetValue(parent.InternalName,out var parts))
                {
                    if(parts.FirstOrDefault(p=>p.Constraints.ContainsKey(Name(capture.Original))) is {} declaration)
                    {
                        var map=Captures(parent).ToDictionary(c=>Name(c.Original),c=>c.Flat,StringComparer.Ordinal);
                        var declared=Parameters(parent);for(var j=0;j<declared.Count;j++)map[Name(declared[j])]=FlatParameter(parent,j);
                        clauses.Add("where "+capture.Flat+":"+string.Join(",",declaration.Constraints[Name(capture.Original)].Select(value=>RewriteTypeIdentifiers(value,n=>map.GetValueOrDefault(n)))));
                    }
                    continue;
                }
                var parentTokens=parent.Unit.Tokens;var p=parentTokens.ToList().FindIndex(t=>t.Location.Offset==parent.Offset)+1;
                for(;p<parentTokens.Count-1&&Offset(parent.Unit,p)<parent.End&&parentTokens[p].Text!="{";p++)
                {
                    if(parentTokens[p].Text!="where"||Name(parentTokens[Next(parent.Unit,p+1)].Text)!=Name(capture.Original))continue;
                    var colon=Next(parent.Unit,Next(parent.Unit,p+1)+1);if(parentTokens[colon].Text!=":")continue;
                    var stop=colon+1;while(stop<parentTokens.Count-1&&parentTokens[stop].Text is not ("where" or "{"))stop++;
                    var raw=TokenText(parent.Unit,colon+1,stop);
                    var mapping=new Dictionary<string,string>(StringComparer.Ordinal);foreach(var item in Captures(parent))mapping[Name(item.Original)]=item.Flat;
                    var declared=Parameters(parent);for(var j=0;j<declared.Count;j++)mapping[Name(declared[j])]=FlatParameter(parent,j);
                    raw=RewriteTypeIdentifiers(raw,n=>mapping.GetValueOrDefault(n));
                    var constraints=SplitTypeList(raw).Select(part=>
                    {
                        if(part is "class" or "struct" or "unmanaged" or "notnull" or "new()")return part;
                        var t=new Lexer(part).Lex();var at=0;var value=TypeSyntax(parent.Unit,t,ref at,ScopeAt(parent.Unit,parent.Offset),parent.Offset,true,false);return at==t.Count-1?value:part;
                    });
                    clauses.Add("where "+capture.Flat+":"+string.Join(",",constraints));break;
                }
            }
            if(clauses.Count>0)unit.Edits.Add(new(insertion,insertion," "+string.Join(" ",clauses)+" ",symbol));
            return true;
        }
        private bool Within(Symbol? actor,Symbol owner)
        {for(var current=actor;current is not null;current=current.Owner)if(current.InternalName==owner.InternalName)return true;return false;}
        private string[]? ContextCaptures(Scope scope,Symbol symbol)
        {
            var captures=Captures(symbol);if(captures.Count==0)return [];
            var actor=scope.TypeOwner;
            if(captures.All(c=>Within(actor,c.Owner)))return captures.Select(c=>actor?.InternalName==c.Owner.InternalName?c.Original:c.Flat).ToArray();
            foreach(var import in VisibleImports(scope).Where(i=>i.Static))
            {
                ResolveImport(import);var (owner,args)=BoundSource(import.BoundTarget!);
                if(owner?.InternalName==symbol.Owner?.InternalName&&args.Length==captures.Count)return args;
            }
            return null;
        }
        private (Symbol? Symbol,string[] Arguments) BoundSource(string syntax)
        {
            var open=syntax.IndexOf('<');var head=open<0?syntax:syntax[..open];var symbol=symbols.FirstOrDefault(s=>s.InternalName==head);
            if(open>=0)
            {
                var depth=0;for(var p=open;p<syntax.Length;p++){if(syntax[p]=='<')depth++;else if(syntax[p]=='>')depth--;if(depth==0&&p!=syntax.Length-1||depth<0)return(null,[]);}
                if(depth!=0)return(null,[]);
            }
            return(symbol,open<0||!syntax.EndsWith('>')?[]:SplitTypeList(syntax[(open+1)..^1]).ToArray());
        }
        private readonly HashSet<Symbol> inheritedTypeLookups=[];
        private (Symbol Symbol,string[] Captures)? SourceNestedType(Unit unit,Scope scope,Symbol owner,IReadOnlyList<string> arguments,string name,int arity,int at,HashSet<Symbol>? visited=null)
        {
            visited??=[];if(!visited.Add(owner)||!inheritedTypeLookups.Add(owner))return null;
            try
            {
            if(inheritedTypeLookups.Count>64)Fail(unit,at,"Nested type inheritance lookup exceeds its nesting limit.","AI3100");
            if(FindVisibleType(unit,s=>s.Owner?.InternalName==owner.InternalName&&s.Name==Name(name)&&s.Arity==arity) is {} child)return(child,arguments.ToArray());
            var tokens=owner.Unit.Tokens;var p=tokens.ToList().FindIndex(t=>t.Location.Offset==owner.Offset)+1;var parens=0;var angles=0;
            for(;p<tokens.Count-1&&Offset(owner.Unit,p)<owner.End;p++)
            {
                var text=tokens[p].Text;if(text is "{" or "where")return null;
                if(text=="(")parens++;else if(text==")")parens--;else if(text=="<")angles++;else if(text==">")angles--;
                if(text!=":"||parens!=0||angles!=0)continue;
                p=Next(owner.Unit,p+1);var syntax=TypeSyntax(owner.Unit,tokens,ref p,ScopeAt(owner.Unit,owner.Offset),owner.Offset,true,false);
                var parameters=Captures(owner).Select(c=>c.Flat).Concat(Parameters(owner)).ToArray();
                if(parameters.Length==arguments.Count)
                {
                    var map=parameters.Select((n,i)=>(Name:Name(n),Value:arguments[i])).ToDictionary(x=>x.Name,x=>x.Value,StringComparer.Ordinal);
                    syntax=RewriteTypeIdentifiers(syntax,n=>map.GetValueOrDefault(n));
                }
                var (parent,bound)=BoundSource(syntax);
                if(parent is not null&&parent.Kind!="interface")return SourceNestedType(unit,scope,parent,bound,name,arity,at,visited);
                return null;
            }
            return null;
            }
            finally{inheritedTypeLookups.Remove(owner);}
        }
        private string NestedReference(Unit unit,Symbol symbol,string path,int at,IReadOnlyList<string> arguments)
            =>TypeReference(unit,symbol,path,at)+(arguments.Count==0?"":"<"+string.Join(",",arguments)+">");
        private string InstantiateSource(Unit unit,Scope scope,Symbol symbol,string sourcePath,IReadOnlyList<string> arguments,int unbound,int at,IReadOnlyList<string>? captures=null)
        {
            _=TypeReference(unit,symbol,sourcePath,at);var capturedCount=Captures(symbol).Count;
            if(unbound>0)return symbol.InternalName+"<"+new string(',',capturedCount+unbound-1)+">";
            captures??=ContextCaptures(scope,symbol);
            if(captures is null)Fail(unit,at,"The containing generic type requires its type arguments.","AI3116");
            var all=captures!.Concat(arguments).ToArray();return symbol.InternalName+(all.Length==0?"":"<"+string.Join(",",all)+">");
        }
    }
}
