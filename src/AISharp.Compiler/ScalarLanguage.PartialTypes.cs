namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class CompilationBinder
    {
        private sealed record PartialHeader(Symbol Symbol,string Kind,string Generic,int PrimaryStart,int PrimaryEnd,int Open,int Close,IReadOnlyList<string> Bases,IReadOnlyDictionary<string,IReadOnlyList<string>> Constraints)
        {public IReadOnlyDictionary<string,(int Start,int End)> BaseArguments {get;init;}=new Dictionary<string,(int,int)>();}
        private readonly Dictionary<string,IReadOnlyList<PartialHeader>> partialTypes=new(StringComparer.Ordinal);
        public static bool ContainsPartialDeclarations(string source)
        {
            if(!source.Contains("partial",StringComparison.Ordinal))return false;
            var tokens=CompilationTokens(source);
            for(var i=0;i<tokens.Count-1;i++)
            {
                if(tokens[i].Kind==TokenKind.String||tokens[i].Text!="partial")continue;
                var next=i+1;while(next<tokens.Count-1&&(tokens[next].Kind==TokenKind.NewLine||Modifiers.Contains(tokens[next].Text)))next++;
                if(TypeWords.Contains(tokens[next].Text)||tokens[next].Text is "fn" or "event")return true;
            }
            return false;
        }
        private PartialHeader ReadPartialHeader(Symbol symbol)
        {
            var unit=symbol.Unit;var tokens=unit.Tokens;
            if(symbol.DeclarationModifiers.Contains("async"))Fail(unit,symbol.Offset,"The async modifier cannot apply to a type declaration.");
            if(symbol.DeclarationModifiers.Distinct(StringComparer.Ordinal).Count()!=symbol.DeclarationModifiers.Count)Fail(unit,symbol.Offset,"Duplicate partial type modifier.");
            if(symbol.Owner is null&&symbol.DeclarationModifiers.Any(m=>m is "private" or "protected"))Fail(unit,symbol.Offset,"A top-level partial type cannot declare private or protected accessibility.");
            var name=tokens.ToList().FindIndex(t=>t.Location.Offset==symbol.Offset);var at=Next(unit,name+1);var generic="";
            if(tokens[at].Text=="<")
            {
                var end=Match(unit,at,"<",">");var values=new List<string>();var parameter=Next(unit,at+1);
                while(parameter<end)
                {
                    var value="";if(tokens[parameter].Text is "in" or "out"){value=tokens[parameter].Text+" ";parameter=Next(unit,parameter+1);}
                    if(!DeclarationIdentifier(tokens[parameter])||values.Any(v=>Name(v.Split(' ')[^1])==Name(tokens[parameter].Text)))Fail(unit,Offset(unit,parameter),"Invalid or duplicate partial type parameter.");
                    value+=tokens[parameter].Text;values.Add(value);parameter=Next(unit,parameter+1);
                    if(parameter<end){if(tokens[parameter].Text!=",")Fail(unit,Offset(unit,parameter),"Expected a type parameter separator.");parameter=Next(unit,parameter+1);if(parameter==end)Fail(unit,Offset(unit,parameter),"A type parameter list cannot end with a separator.");}
                }
                generic=string.Join(",",values);at=Next(unit,end+1);
            }
            var primaryStart=-1;var primaryEnd=-1;
            if(tokens[at].Text=="("){primaryStart=Offset(unit,at);var end=Match(unit,at,"(",")");primaryEnd=Offset(unit,end)+1;at=Next(unit,end+1);}
            var bases=new List<string>();var baseArguments=new Dictionary<string,(int Start,int End)>(StringComparer.Ordinal);
            if(tokens[at].Text==":")
            {
                at=Next(unit,at+1);
                while(true)
                {
                    var start=at;var type=TypeSyntax(unit,tokens,ref at,symbol.Scope,Offset(unit,start),directives:true,strict:true);
                    if(at==start)Fail(unit,Offset(unit,start),"A partial type base must name a bound type.");
                    if(bases.Contains(type))Fail(unit,Offset(unit,start),"A base type cannot be repeated in one partial declaration.");
                    if(BoundSource(type).Symbol?.Kind is "class" or "record"&&bases.Count>0)Fail(unit,Offset(unit,start),"A base class must precede interface bases.");
                    bases.Add(type);at=Next(unit,at);
                    if(tokens[at].Text=="(")
                    {
                        if(symbol.Kind!="record"||primaryStart<0)Fail(unit,Offset(unit,at),"A record base argument list requires its primary declaration.");
                        var close=Match(unit,at,"(",")");baseArguments.Add(type,(Offset(unit,at),Offset(unit,close)+1));at=Next(unit,close+1);
                    }
                    if(tokens[at].Text!=",")break;at=Next(unit,at+1);
                }
            }
            var constraints=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
            while(tokens[at].Text=="where")
            {
                at=Next(unit,at+1);var parameter=tokens[at];at=Next(unit,at+1);
                if(tokens[at].Text!=":"||!Parameters(symbol).Select(Name).Contains(Name(parameter.Text)))Fail(unit,parameter.Location.Offset,"A partial type constraint must name one of its own parameters.");
                if(constraints.ContainsKey(Name(parameter.Text)))Fail(unit,parameter.Location.Offset,"Duplicate partial type constraint clause.");
                at=Next(unit,at+1);var values=new List<string>();
                while(true)
                {
                    var start=at;string value;
                    if(tokens[at].Text=="new"&&tokens[Next(unit,at+1)].Text=="(")
                    {var open=Next(unit,at+1);var close=Next(unit,open+1);if(tokens[close].Text!=")")Fail(unit,Offset(unit,close),"A constructor constraint requires new().");value="new()";at=Next(unit,close+1);}
                    else if(tokens[at].Text is "class" or "struct" or "unmanaged" or "notnull")
                    {value=tokens[at].Text;at=Next(unit,at+1);if(tokens[at].Text=="?"){value+="?";at=Next(unit,at+1);}}
                    else{value=TypeSyntax(unit,tokens,ref at,symbol.Scope,Offset(unit,start),directives:true,strict:true);at=Next(unit,at);if(at==start)Fail(unit,Offset(unit,start),"Invalid partial type constraint.");}
                    if(values.Contains(value))Fail(unit,Offset(unit,start),"Duplicate partial type constraint.");
                    if(value is "class" or "class?" or "struct" or "unmanaged" or "notnull"&&values.Count>0||values.Contains("new()"))Fail(unit,Offset(unit,start),"Partial type constraints are out of order.");
                    if(value=="new()"&&values.Any(v=>v is "struct" or "unmanaged"))Fail(unit,Offset(unit,start),"The new() constraint cannot be combined with a value-type constraint.");
                    values.Add(value);if(tokens[at].Text!=",")break;at=Next(unit,at+1);
                }
                constraints.Add(Name(parameter.Text),values);
            }
            var openOffset=-1;var closeOffset=-1;
            if(tokens[at].Text=="{"){openOffset=Offset(unit,at);closeOffset=Offset(unit,Match(unit,at,"{","}"));}
            else if(symbol.Kind!="record"||tokens[at].Text is not (";" or "\n" or ""))Fail(unit,Offset(unit,at),"A partial type requires a body, or a positional record declaration.");
            var previous=name-1;while(previous>=0&&tokens[previous].Kind==TokenKind.NewLine)previous--;
            var kind=symbol.Kind=="record"&&tokens[previous].Text=="struct"?"record struct":symbol.Kind;
            return new(symbol,kind,generic,primaryStart,primaryEnd,openOffset,closeOffset,bases,constraints){BaseArguments=baseArguments};
        }
        private static string PartialParameterIdentity(string generic)=>string.Join(",",generic.Split(',').Select(p=>p.Contains(' ')?p[..(p.LastIndexOf(' ')+1)]+Name(p[(p.LastIndexOf(' ')+1)..]):Name(p)));
        private void ValidatePartialTypes()
        {
            foreach(var group in symbols.Where(s=>s.Partial).GroupBy(s=>s.InternalName))
            {
                var parts=group.Select(ReadPartialHeader).ToArray();var first=parts[0];
                if(parts.Any(p=>p.Kind!=first.Kind||PartialParameterIdentity(p.Generic)!=PartialParameterIdentity(first.Generic)))Fail(parts[^1].Symbol.Unit,parts[^1].Symbol.Offset,"Partial type kinds and type parameter declarations must agree.");
                var accesses=parts.Select(p=>string.Join(" ",p.Symbol.DeclarationModifiers.Where(m=>m is "public" or "private" or "protected" or "internal").Order(StringComparer.Ordinal))).Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).ToArray();
                if(accesses.Length>1)Fail(parts[^1].Symbol.Unit,parts[^1].Symbol.Offset,"Partial type accessibility declarations conflict.");
                var modifiers=parts.SelectMany(p=>p.Symbol.DeclarationModifiers).ToHashSet(StringComparer.Ordinal);
                if(modifiers.Contains("abstract")&&modifiers.Contains("sealed")||modifiers.Contains("static")&&(modifiers.Contains("abstract")||modifiers.Contains("sealed")))Fail(first.Symbol.Unit,first.Symbol.Offset,"Partial type modifiers form an invalid combination.");
                if(parts.Count(p=>p.PrimaryStart>=0)>1)Fail(parts.Last(p=>p.PrimaryStart>=0).Symbol.Unit,parts.Last(p=>p.PrimaryStart>=0).Symbol.Offset,"Only one partial type declaration can contain primary parameters.");
                foreach(var parameter in Parameters(first.Symbol).Select(Name))
                {
                    var declarations=parts.Where(p=>p.Constraints.ContainsKey(parameter)).ToArray();
                    if(declarations.Length>1&&declarations.Skip(1).Any(p=>!p.Constraints[parameter].ToHashSet(StringComparer.Ordinal).SetEquals(declarations[0].Constraints[parameter])))Fail(declarations[^1].Symbol.Unit,declarations[^1].Symbol.Offset,"Partial type constraints disagree for '"+parameter+"'.");
                }
                var classBases=parts.SelectMany(p=>p.Bases.Select(b=>(Part:p,Text:b,Type:BoundSource(b).Symbol))).Where(x=>x.Type is {Kind:"class"}||x.Type?.Kind=="record"&&!x.Part.Kind.EndsWith("struct",StringComparison.Ordinal)).ToArray();
                if(classBases.Select(x=>x.Text).Distinct(StringComparer.Ordinal).Count()>1)Fail(classBases[^1].Part.Symbol.Unit,classBases[^1].Part.Symbol.Offset,"Partial type base classes disagree.");
                if(accesses.Length==1)foreach(var part in parts)part.Symbol.Accessibility=accesses[0]=="internal protected"?"protected internal":accesses[0]=="private protected"?"private protected":accesses[0];
                partialTypes.Add(group.Key,Array.AsReadOnly(parts));
            }
        }
        private void PartialTypeHeader(IReadOnlyList<PartialHeader> parts,Action<Unit,int,int,string> append,Action<Unit,int,int,Symbol?> emit)
        {
            var first=parts[0];var symbol=first.Symbol;var modifiers=parts.SelectMany(p=>p.Symbol.DeclarationModifiers).Where(m=>m is "abstract" or "sealed" or "static" or "readonly").Distinct(StringComparer.Ordinal);
            var parameters=Captures(symbol).Select(c=>c.Flat).Concat(first.Generic.Length==0?[]:first.Generic.Split(',')).ToArray();
            var text=string.Join(" ",modifiers);if(text.Length>0)text+=" ";text+=first.Kind+" "+symbol.InternalName+(parameters.Length==0?"":"<"+string.Join(",",parameters)+">");
            void Generated(string value)=>append(symbol.Unit,symbol.Offset,symbol.Offset+1,value);
            Generated(text);text="";
            if(parts.FirstOrDefault(p=>p.PrimaryStart>=0) is {} primary)emit(primary.Symbol.Unit,primary.PrimaryStart,primary.PrimaryEnd,primary.Symbol);
            var bases=parts.SelectMany(p=>p.Bases).Distinct(StringComparer.Ordinal).OrderBy(b=>BoundSource(b).Symbol?.Kind is "class" or "record"?0:1).ToArray();
            for(var i=0;i<bases.Length;i++)
            {
                var baseType=bases[i];Generated((i==0?":":",")+baseType);
                if(parts.FirstOrDefault(p=>p.BaseArguments.ContainsKey(baseType)) is {} basePart)
                {var range=basePart.BaseArguments[baseType];emit(basePart.Symbol.Unit,range.Start,range.End,basePart.Symbol);}
            }
            foreach(var capture in Captures(symbol))
            {
                var ownerParts=partialTypes.GetValueOrDefault(capture.Owner.InternalName)??[ReadPartialHeader(capture.Owner)];
                if(ownerParts.FirstOrDefault(p=>p.Constraints.ContainsKey(Name(capture.Original))) is not {} declaration)continue;
                var map=Captures(capture.Owner).ToDictionary(c=>Name(c.Original),c=>c.Flat,StringComparer.Ordinal);
                var own=Parameters(capture.Owner);for(var i=0;i<own.Count;i++)map[Name(own[i])]=FlatParameter(capture.Owner,i);
                var constraints=declaration.Constraints[Name(capture.Original)].Select(value=>RewriteTypeIdentifiers(value,name=>map.GetValueOrDefault(name)));
                text+=" where "+capture.Flat+":"+string.Join(",",constraints);
            }
            foreach(var parameter in Parameters(symbol).Select(Name))if(parts.FirstOrDefault(p=>p.Constraints.ContainsKey(parameter)) is {} owner)text+=" where "+parameter+":"+string.Join(",",owner.Constraints[parameter]);
            Generated(text+" {\n");
        }
    }
}
