using System.Text;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class CompilationBinder
    {
        private static IEnumerable<string> NamespaceChain(string name)
        {
            while (true) { yield return name; var dot = name.LastIndexOf('.'); if (dot < 0) { if (name.Length != 0) yield return ""; break; } name = name[..dot]; }
        }
        private Scope ScopeAt(Unit unit, int offset) => unit.Scopes.Where(s => s.Start <= offset && offset < s.End).OrderByDescending(s => s.Start).ThenBy(s => s.End).First();
        private static string Qualify(string space, string name) => space.Length == 0 ? name : space + "." + name;
        private bool Shadowed(Unit unit, int at, string name, bool type) => unit.Shadows.Any(s => s.Type == type && s.Start <= at && at < s.End && s.Name == name);
        private IEnumerable<Import> VisibleImports(Scope scope)
        {
            for (var current = scope; current is not null; current = current.Parent) foreach (var import in current.Imports.Where(i => !i.Global)) yield return import;
            foreach (var import in globals) yield return import;
        }
        private Import? Alias(Scope scope, string name) => VisibleImports(scope).FirstOrDefault(i => i.Name == name);
        private string? NamespacePath(Scope scope, string path, bool directives = true)
        {
            var global = path.StartsWith("global::", StringComparison.Ordinal); var raw = global ? path[8..] : path; var colon = raw.IndexOf("::", StringComparison.Ordinal); var dot = raw.IndexOf('.');
            var split = colon >= 0 ? colon : dot;
            if (!global && directives && split > 0 && Alias(scope, Name(raw[..split])) is { } alias)
            {
                ResolveImport(alias); if (!alias.NamespaceTarget) return null;
                raw = alias.BoundTarget + "." + raw[(split + (colon >= 0 ? 2 : 1))..]; global = true;
            }
            var firstPart=raw.Split('.')[0];
            if(!global)
            {
                if(FindType(scope,firstPart,0) is not null)return null;
                if(directives&&TypePath(scope.Unit,scope,firstPart,0,scope.Start,true,false) is {} imported&&symbols.Any(s=>s.InternalName==imported))return null;
            }
            var canonical = Canonical(raw);
            foreach (var space in global ? new[] { "" } : NamespaceChain(scope.Name))
            {
                var candidate = Qualify(space, canonical);
                if (namespaces.Contains(candidate) || QualifiedBuiltinTypes.Keys.Any(k => k.StartsWith(candidate + ".", StringComparison.Ordinal))) return candidate;
            }
            return null;
        }
        private void ResolveImport(Import import)
        {
            if (import.BoundTarget is not null) return;
            if (import.Resolving) Fail(import.Scope.Unit, import.Offset, "Circular import target.");
            import.Resolving = true;
            try
            {
                var targetScope = new Scope(import.Scope.Unit,import.Scope.Name,import.Scope.Start,import.Scope.End,import.Global?null:import.Scope.Parent);
                var space = import.Static?null:NamespacePath(targetScope, import.Target, directives: !import.Global);
                var directType = FindType(targetScope, import.Target, 0);
                if (space is not null && directType is null) { import.BoundTarget = space; import.NamespaceTarget = true; return; }
                if (import.Name is null&&!import.Static) Fail(import.Scope.Unit, import.Offset, "Unresolved imported namespace '" + import.Target + "'.");
                var tokens = new Lexer(import.Target).Lex(); var position = 0;
                var bound = TypeSyntax(import.Scope.Unit, tokens, ref position, targetScope, import.Offset, directives: !import.Global, strict: true);
                if (position != tokens.Count - 1 || bound is "void" or "unit" or "global::System.Void") Fail(import.Scope.Unit, import.Offset, "An alias must target a supported non-void type or namespace.");
                import.BoundTarget = bound;
                if(import.Global&&import.Static&&ContainsFileType(bound))Fail(import.Scope.Unit,import.Offset,"A global static import cannot target a file-local type.");
            }
            finally { import.Resolving = false; }
        }
        private Symbol? FindType(Scope scope, string path, int arity)
        {
            var global = path.StartsWith("global::", StringComparison.Ordinal); var canonical = Canonical(path);
            foreach (var space in global ? new[] { "" } : NamespaceChain(scope.Name))
            {
                var candidate = Qualify(space, canonical); var symbol = FindVisibleType(scope.Unit,s => s.QualifiedName == candidate && s.Arity == arity,scope); if (symbol is not null) return symbol;
            }
            return null;
        }
        private string? TypePath(Unit unit, Scope scope, string path, int arity, int at, bool directives, bool strict)
        {
            var global = path.StartsWith("global::", StringComparison.Ordinal); var raw = global ? path[8..] : path;
            var colon = raw.IndexOf("::", StringComparison.Ordinal); var split = colon >= 0 ? colon : raw.IndexOf('.');
            var simple = split < 0; var first = simple ? raw : raw[..split]; var name = Name(first);
            if (simple && !global && CanUsePrimitive(raw)) return raw;
            if (simple && !global && Shadowed(unit, at, name, true)) return ParameterReplacement(unit,at,name)??raw;
            if(simple&&raw.StartsWith("__unit_capture_",StringComparison.Ordinal)&&symbols.SelectMany(Captures).Any(c=>c.Flat==raw))return raw;
            if(simple&&!global)
            {
                foreach(var space in NamespaceChain(scope.Name))
                {
                    var local=FindVisibleType(unit,s=>s.QualifiedName==Qualify(space,name)&&s.Arity==arity,scope);
                    if(local is not null)return TypeReference(unit,local,path,at);
                    if(!directives)continue;
                    var lexical=ScopeChain(scope).FirstOrDefault(s=>s.Name==space);if(lexical is null)continue;
                    var imports=ScopeImports(lexical).ToArray();
                    if(imports.FirstOrDefault(i=>i.Name==name) is {} localAlias)
                    {
                        ResolveImport(localAlias);if(localAlias.NamespaceTarget){if(strict)Fail(unit,at,"A namespace alias is not a type.");return null;}
                        if(arity!=0)Fail(unit,at,"A closed type alias is not a generic type definition.");CheckFileTypeUse(unit,localAlias.BoundTarget!,at);return localAlias.BoundTarget;
                    }
                    var found=ImportedTypes(unit,scope,imports,name,arity,at,path);
                    if(found.Count>1)Fail(unit,at,"Ambiguous imported type '"+name+"'.");if(found.Count==1)return found[0];
                }
            }
            var own = FindVisibleType(unit,s => s.QualifiedName == Qualify(scope.Name, Canonical(raw)) && s.Arity == arity,scope);
            if (own is not null) return TypeReference(unit,own,path,at);
            if (!global && directives && Alias(scope, name) is { } alias)
            {
                ResolveImport(alias);
                if (split < 0)
                {
                    if (alias.NamespaceTarget) { if (strict) Fail(unit, at, "A namespace alias is not a type."); return null; }
                    if (arity != 0) Fail(unit, at, "A closed type alias is not a generic type definition.");
                    CheckFileTypeUse(unit,alias.BoundTarget!,at);return alias.BoundTarget;
                }
                if (!alias.NamespaceTarget)
                {
                    CheckFileTypeUse(unit,alias.BoundTarget!,at);
                    if(colon>=0)Fail(unit,at,"A namespace qualifier requires a namespace alias.");
                    var (owner,captured)=BoundSource(alias.BoundTarget!);
                    var nested=owner is null?null:FindVisibleType(unit,s=>s.Owner?.InternalName==owner.InternalName&&s.QualifiedName==owner.QualifiedName+"."+Canonical(raw[(split+1)..])&&s.Arity==arity);
                    return nested is not null?TypeReference(unit,nested,path,at)+(captured.Length==0?"":"<"+string.Join(",",captured)+">"):alias.BoundTarget+"."+raw[(split+1)..];
                }
                raw = alias.BoundTarget + "." + raw[(split + (colon >= 0 ? 2 : 1))..]; global = true;
            }
            else if (colon >= 0) { if (strict) Fail(unit, at, "Unresolved namespace alias qualifier."); return null; }
            if(!global&&!simple)
            {
                var owner=FindType(scope,first,0);
                if(owner is null&&directives)
                {var imported=TypePath(unit,scope,first,0,at,true,false);owner=symbols.FirstOrDefault(s=>s.InternalName==imported);}
                if(owner is not null)
                {
                    var nested=FindVisibleType(unit,s=>s.Owner?.InternalName==owner.InternalName&&s.QualifiedName==owner.QualifiedName+"."+Canonical(raw[(split+1)..])&&s.Arity==arity);
                    if(nested is not null)return TypeReference(unit,nested,path,at);
                    if(strict)Fail(unit,at,"A source type shadows this namespace path; the nested type is unresolved.");return owner.InternalName+"."+raw[(split+1)..];
                }
            }
            var symbol = FindType(scope, (global ? "global::" : "") + raw, arity); if (symbol is not null) return TypeReference(unit,symbol,path,at);
            if(simple&&!global)
                for(var context=scope.TypeOwner;context is not null;context=context.Owner)
                {
                    var supplied=Captures(context).Select(c=>c.Flat).Concat(Parameters(context)).ToArray();
                    if(SourceNestedType(unit,scope,context,supplied,raw,arity,at) is {} inherited)return NestedReference(unit,inherited.Symbol,path,at,inherited.Captures);
                }
            for(var dot=raw.LastIndexOf('.');dot>0;dot=raw.LastIndexOf('.',dot-1))
            {
                var prefix=(global?"global::":"")+raw[..dot];var suffix=raw[(dot+1)..];if(suffix.Contains('.'))continue;
                if(FindType(scope,prefix,0) is {} parent&&SourceNestedType(unit,scope,parent,ContextCaptures(scope,parent)??[],suffix,arity,at) is {} inherited)
                    return NestedReference(unit,inherited.Symbol,path,at,inherited.Captures);
            }
            var canonical = Canonical(raw);
            if (QualifiedBuiltinTypes.ContainsKey(canonical)) return "global::" + canonical;
            if (simple && raw is "nint" or "nuint" or "IDisposable" or "Task" or "TaskCompletionSource" or "IEnumerable" or "IEnumerator" or "IEquatable" or "EqualityComparer" or "IEqualityComparer" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList" or "Type") return raw;
            if (strict) Fail(unit, at, "Unresolved type '" + path + "'.");
            return null;
        }
        private static bool CanUsePrimitive(string raw) => BuiltInTypeNames.Contains(raw, StringComparer.Ordinal);
        private string TypeReference(Unit unit,Symbol symbol,string path,int at)
        {
            var leaf=System.Text.RegularExpressions.Regex.Split(path,@"\.|::")[^1];
            if(leaf.Length>0&&leaf[0]!='@'&&Name(leaf)==leaf&&ReservedTypeParameterNames.Contains(leaf))Fail(unit,at,"A reserved type name requires an escaped identifier.");
            return symbol.InternalName;
        }
        private string TypeSyntax(Unit unit, IReadOnlyList<Token> tokens, ref int position, Scope scope, int at, bool directives, bool strict)
        {
            var begin = position;
            if(tokens[position].Text=="(")
            {
                position++;var elements=new List<string>();
                while(position<tokens.Count-1)
                {
                    var before=position;var element=TypeSyntax(unit,tokens,ref position,scope,at,directives,strict);
                    if(before==position){position=begin;return "";}
                    if(Identifier(tokens[position])&&tokens[position+1].Text is "," or ")")element+=" "+tokens[position++].Text;
                    elements.Add(element);if(tokens[position].Text!=",")break;position++;
                }
                if(elements.Count<2||tokens[position].Text!=")"){position=begin;return "";}position++;return TypeSuffix(tokens,ref position,"("+string.Join(",",elements)+")");
            }
            if (position >= tokens.Count || !Identifier(tokens[position])) return "";
            var name=tokens[position++].Text;
            while(position<tokens.Count-1)
            {
                if(tokens[position].Text==":"&&position+2<tokens.Count&&tokens[position+1].Text==":"&&Identifier(tokens[position+2])){name+="::"+tokens[position+2].Text;position+=3;}
                else if(tokens[position].Text=="."&&Identifier(tokens[position+1])){name+="."+tokens[position+1].Text;position+=2;}
                else break;
            }
            if(!ReadTypeArguments(unit,tokens,ref position,scope,at,directives,strict,out var arguments,out var unbound)){position=begin;return "";}
            var bound=TypePath(unit,scope,name,unbound==0?arguments.Count:unbound,at,directives,strict);
            if(bound is null){position=begin;return "";}
            var (source,existing)=BoundSource(bound);
            IReadOnlyList<string> complete=arguments;
            if(source is not null)
            {
                var count=Captures(source).Count;
                if(existing.Length>0&&arguments.Count==0&&existing.Length==count+source.Arity){complete=existing;}
                else
                {
                    IReadOnlyList<string>? captured=existing.Length==count?existing:null;
                    bound=InstantiateSource(unit,scope,source,name,arguments,unbound,at,captured);
                    complete=unbound>0?[]:BoundSource(bound).Arguments;
                }
                while(tokens[position].Text=="."&&position+1<tokens.Count&&Identifier(tokens[position+1]))
                {
                    var saved=position;var childName=tokens[position+1].Text;position+=2;
                    if(!ReadTypeArguments(unit,tokens,ref position,scope,at,directives,strict,out var childArguments,out var childUnbound)){position=saved;break;}
                    var nested=SourceNestedType(unit,scope,source,complete,childName,childUnbound==0?childArguments.Count:childUnbound,at);
                    if(nested is null){position=saved;break;}var child=nested.Value.Symbol;
                    if(unbound>0&&childArguments.Count>0||unbound==0&&complete.Count>0&&childUnbound>0)
                        Fail(unit,at,"An unbound nested type name cannot mix type arguments and empty type argument lists.");
                    var totalUnbound=unbound>0?Captures(child).Count+child.Arity:0;
                    if(totalUnbound>0){if(strict)Fail(unit,at,"An alias cannot target an unbound generic definition.");bound=child.InternalName+"<"+new string(',',totalUnbound-1)+">";unbound=totalUnbound;}
                    else bound=InstantiateSource(unit,scope,child,childName,childArguments,childUnbound,at,nested.Value.Captures);
                    source=child;complete=unbound>0?[]:BoundSource(bound).Arguments;
                }
            }
            else if(unbound!=0)bound+="<"+new string(',',unbound-1)+">";
            else if(arguments.Count!=0)bound+="<"+string.Join(",",arguments)+">";
            return TypeSuffix(tokens,ref position,bound);

        }
        private bool ReadTypeArguments(Unit unit,IReadOnlyList<Token> tokens,ref int position,Scope scope,int at,bool directives,bool strict,out List<string> values,out int arity)
        {
                values=[];arity=0;if(tokens[position].Text!="<")return true;position++;
                if(tokens[position].Text is "," or ">")
                {
                    arity=1;while(tokens[position].Text==","){arity++;position++;}
                    if(tokens[position].Text!=">")return false;position++;
                    if(strict)Fail(unit,at,"An alias cannot target an unbound generic definition.");return true;
                }
                do
                {
                    var before=position;var value=TypeSyntax(unit,tokens,ref position,scope,at,directives,strict);
                    if(before==position)return false;values.Add(value);if(tokens[position].Text!=",")break;position++;
                }while(position<tokens.Count-1);
                if(tokens[position].Text!=">"){if(strict)Fail(unit,at,"Unterminated type arguments.");return false;}position++;return true;
        }
        private static string TypeSuffix(IReadOnlyList<Token> tokens,ref int position,string syntax)
        {
            while(position<tokens.Count-1)
            {
                if(tokens[position].Text=="?"&&tokens[position+1].Text!="?"){syntax+="?";position++;continue;}
                if(tokens[position].Text!="[")break;var next=position+1;while(tokens[next].Text==",")next++;
                if(tokens[next].Text!="]")break;syntax+="["+new string(',',next-position-1)+"]";position=next+1;
            }
            return syntax;
        }
        private string Internal(Symbol symbol) => ReferenceEquals(symbol, entrySymbol) ? "main" : symbol.InternalName;
        private Symbol? FunctionPath(Scope scope, string path)
        {
            var global = path.StartsWith("global::", StringComparison.Ordinal); var raw = global ? path[8..] : path; var split = raw.IndexOf('.'); var colon = raw.IndexOf("::", StringComparison.Ordinal); if (colon >= 0) split = colon;
            if (!global && split > 0 && Alias(scope, Name(raw[..split])) is { } alias)
            {
                ResolveImport(alias); if (!alias.NamespaceTarget) return null; raw = alias.BoundTarget + "." + raw[(split + (colon >= 0 ? 2 : 1))..]; global = true;
            }
            foreach (var space in global ? new[] { "" } : NamespaceChain(scope.Name))
            { var name = Qualify(space, Canonical(raw)); if (symbols.FirstOrDefault(s => s.Function && s.QualifiedName == name) is { } found) { var leaf=System.Text.RegularExpressions.Regex.Split(raw,@"\.|::")[^1]; if(leaf[0]!='@'&&Name(leaf)==leaf&&ReservedTypeParameterNames.Contains(leaf))return null; return found; } }
            return null;
        }
        private void BuildSource()
        {
            var text = new StringBuilder("module " + string.Join(".",result.EntryModule.Split('.').Select(p=>IdentifierSpelling.Valid(p,allowAt:false)?"@"+p:p)) + "\nintent \"Native compilation group\"\n");
            var aliasIndex=0;
            foreach(var import in units.SelectMany(u=>u.Scopes).SelectMany(s=>s.Imports).Where(i=>i.Static||i.Name is not null&&!i.NamespaceTarget))
            {
                text.Append("using __unit_alias_").Append(aliasIndex++).Append("=");var start=text.Length;
                text.Append(import.BoundTarget);result.Segments.Add(new(start,text.Length,import.Scope.Unit.Index,import.Offset,1));text.Append(";\n");
            }
            var emittedPartialTypes=new HashSet<string>(StringComparer.Ordinal);
            foreach(var unit in units)
            {
                EmitRange(unit,0,unit.Input.Source.Length,null);text.Append('\n');
                foreach(var declaration in symbols.Where(s=>s.Unit==unit&&(s.Owner is not null||s.Partial)))
                {
                    if(declaration.Partial)
                    {
                        if(!emittedPartialTypes.Add(declaration.InternalName))continue;
                        var parts=partialTypes[declaration.InternalName];var anchor=parts[0].Symbol;
                        PartialTypeHeader(parts,Append,EmitRange);
                        foreach(var part in parts)if(part.Open>=0){EmitRange(part.Symbol.Unit,part.Open+1,part.Close,part.Symbol);text.Append('\n');}
                        Append(anchor.Unit,anchor.End-1,anchor.End,"}\n");
                    }
                    else EmitRange(unit,declaration.Start,declaration.End,declaration);
                    text.Append('\n');
                }
            }
            void EmitRange(Unit unit,int start,int end,Symbol? owner)
            {
                var candidates=symbols.Where(s=>s.Unit==unit&&s.Start>=start&&s.End<=end&&(s.Owner is not null&&(owner is null?s.Owner.Owner is null:ReferenceEquals(s.Owner,owner))||owner is null&&s.Owner is null&&s.Partial)).ToArray();
                var omitted=candidates.Where(s=>!candidates.Any(other=>!ReferenceEquals(s,other)&&other.Start<=s.Start&&s.End<=other.End)).ToArray();
                var edits=unit.Edits.Where(e=>e.DeclarationOwner is {} declaration?ReferenceEquals(declaration,owner)&&e.Start>=start&&e.End<=end:e.Start>=start&&e.Start<end&&e.End<=end&&!omitted.Any(s=>s.Start<=e.Start&&e.Start<s.End)).ToList();
                edits.AddRange(omitted.Select(s=>new Edit(s.Start,s.End,Mask(unit.Input.Source[s.Start..s.End]))));
                var at=start;
                foreach(var edit in edits.OrderBy(e=>e.Start).ThenBy(e=>e.End))
                {
                    if(edit.Start<at)Fail(unit,edit.Start,"Overlapping compilation binding edits.");
                    Append(unit,at,edit.Start,unit.Input.Source[at..edit.Start]);Append(unit,edit.Start,edit.End,edit.Text);at=edit.End;
                }
                Append(unit,at,end,unit.Input.Source[at..end]);
            }
            result.Source = text.ToString();
            T? ResolveAt<T>(int offset,Func<Unit,Scope,int,T?> resolve)
            {
                var segment=result.Segments.LastOrDefault(s=>s.Start<=offset&&offset<s.End);if(segment is null)return default;
                var unit=units[segment.Unit];var original=segment.OriginalStart+Math.Min(Math.Max(0,offset-segment.Start),Math.Max(0,segment.OriginalLength-1));
                try{return resolve(unit,ScopeAt(unit,original),original);}
                catch(BindingFailure failure){throw DeclarationError(failure.Code,failure.Message,LocationAt(result.Source,offset));}
            }
            result.TypeResolver=(path,arity,offset)=>ResolveAt<string>(offset,(unit,scope,at)=>TypePath(unit,scope,path,arity,at,directives:true,strict:false));
            result.TypeNormalizer=(syntax,offset)=>ResolveAt<string>(offset,(unit,scope,at)=>
            {var tokens=new Lexer(syntax).Lex();var position=0;var bound=TypeSyntax(unit,tokens,ref position,scope,at,directives:true,strict:false);return position==tokens.Count-1?bound:null;});
            result.AliasResolver=(name,offset)=>ResolveAt<(string Target,bool Namespace)?>(offset,(unit,scope,at)=>
            {if(Alias(scope,Name(name)) is not {} import)return null;CheckFileTypeUse(unit,import.BoundTarget!,at);return(import.BoundTarget!,import.NamespaceTarget);});
            result.NamespaceResolver=(name,offset)=>ResolveAt<string>(offset,(_,scope,_)=>NamespacePath(scope,name));
            result.FunctionResolver=(name,offset)=>ResolveAt<string>(offset,(_,scope,_)=>FunctionPath(scope,name) is {} symbol?Internal(symbol):null);
            result.StaticImportResolver=offset=>ResolveAt<IReadOnlyList<ScalarStaticImportScope>>(offset,(_,scope,_)=>Array.AsReadOnly(ScopeChain(scope).Select(StaticImportScope).ToArray()))??[];
            result.TypeOwnerResolver=offset=>ResolveAt<string>(offset,(_,scope,_)=>scope.TypeOwner?.InternalName);
            result.PartialParameterResolver=offset=>ResolveAt<IReadOnlyList<string>>(offset,(unit,_,at)=>partialMembers.FirstOrDefault(p=>p.Body.Owner.Unit==unit&&p.Body.Start<=at&&at<p.Body.End).Definition?.Parameters.Select(p=>Name(p.Name)).ToArray());
            result.PartialTypeParameterResolver=offset=>ResolveAt<IReadOnlyList<string>>(offset,(unit,_,at)=>partialMembers.FirstOrDefault(p=>p.Body.Owner.Unit==unit&&p.Body.Start<=at&&at<p.Body.End).Definition?.Generic.Select(Name).ToArray());
            result.HasOmittedPartialMembers=partialMembers.Any(p=>p.Omitted);
            result.OmittedPartialResolver=offset=>ResolveAt<bool?>(offset,(unit,_,at)=>partialMembers.Any(p=>p.Omitted&&p.Body.Owner.Unit==unit&&p.Body.Start<=at&&at<p.Body.End))==true;
            void Append(Unit unit, int start, int end, string value)
            {
                if (value.Length == 0) return; var at = text.Length; text.Append(value); result.Segments.Add(new(at, text.Length, unit.Index, start, end - start));
            }
        }
    }
}
