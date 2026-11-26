using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record SourceImport(string? Name,string Target,bool Global,SourceLocation Location,SourceLocation TargetLocation);
    private sealed class SourceImportScope
    {
        public string Module="";
        public List<SourceImport> Directives=[];
        public List<(int Start,int End)> Ranges=[];
        public Dictionary<string,ScalarType> Types=new(StringComparer.Ordinal);
        public int Resolving;
    }
    [ThreadStatic] private static SourceImportScope? sourceImports;
    private static string CanonicalPath(string value)=>string.Join(".",Regex.Replace(value,@"\s+","").Split('.').Select(IdentifierName));
    private static bool KnownImportNamespace(string value)
    {
        value=CanonicalPath(value);if(value.StartsWith("global::",StringComparison.Ordinal))value=value[8..];
        return value==sourceImports?.Module||QualifiedBuiltinTypes.Keys.Any(k=>k.StartsWith(value+".",StringComparison.Ordinal));
    }
    private static SourceImportScope ReadSourceImports(string source,ScalarFeatures features,out Diagnostic? error)
    {
        var scope=new SourceImportScope();error=null;if((features&ScalarFeatures.Imports)==0)return scope;
        var tokens=new Lexer(source).Lex();var depth=0;var members=false;var regular=false;
        for(var i=0;i<tokens.Count-1;i++)
        {
            var token=tokens[i];if(token.Kind==TokenKind.LBrace){depth++;continue;}if(token.Kind==TokenKind.RBrace){depth--;continue;}if(depth!=0)continue;
            if(token.Kind is TokenKind.Module or TokenKind.Intent)
            {
                var start=++i;while(i<tokens.Count&&tokens[i].Kind is not (TokenKind.NewLine or TokenKind.EndOfFile))i++;
                if(token.Kind==TokenKind.Module)scope.Module=CanonicalPath(string.Concat(tokens.Skip(start).Take(i-start).Select(t=>t.Text)));
                continue;
            }
            var following=i+1;while(following<tokens.Count-1&&tokens[following].Kind==TokenKind.NewLine)following++;
            var global=token.Text=="global"&&tokens[following].Text=="using";
            if(token.Text!="using"&&!global)
            {if(token.Kind==TokenKind.Fn||token.Text is "class" or "struct" or "interface" or "enum" or "record" or "delegate")members=true;continue;}
            var begin=token.Location.Offset;var p=global?following+1:i+1;var last=p;
            while(last<tokens.Count&&tokens[last].Text!=";"&&tokens[last].Kind!=TokenKind.EndOfFile)
            {if(tokens[last].Kind==TokenKind.Fn||tokens[last].Text is "{" or "}")break;last++;}
            var complete=last<tokens.Count&&tokens[last].Text==";";
            var end=complete?tokens[last].Location.Offset+1:source.IndexOf('\n',begin);if(end<0)end=source.Length;scope.Ranges.Add((begin,end));
            try
            {
                if(!complete)throw DeclarationError("AI3106","An import directive must end with ';'.",token.Location);
                if(members)throw DeclarationError("AI3106","Imports must precede type and function declarations.",token.Location);
                if(global&&regular)throw DeclarationError("AI3106","Global imports must precede ordinary imports.",token.Location);
                if(!global)regular=true;
                var items=tokens.Skip(p).Take(last-p).Where(t=>t.Kind!=TokenKind.NewLine).ToArray();
                if(items.Length==0)throw DeclarationError("AI3106","Expected an import target.",token.Location);
                string? name=null;var targetAt=items[0].Location;
                if(items.Length>1&&items[1].Kind==TokenKind.Equals)
                {
                    var raw=items[0].Text;
                    var identifier=IsEscapedIdentifier(raw)||((features&ScalarFeatures.UnicodeIdentifiers)!=0?IdentifierSpelling.Valid(raw,allowAt:false):Regex.IsMatch(raw,@"^[A-Za-z_][A-Za-z0-9_]*$"));
                    if(!identifier||!IsEscapedIdentifier(raw)&&(ReservedTypeParameterNames.Contains(raw)||raw is "fn"))throw DeclarationError("AI3106","Invalid import alias name.",items[0].Location);
                    name=IdentifierName(raw);if(scope.Directives.Any(d=>d.Name==name))throw DeclarationError("AI3106",$"Duplicate import alias '{name}'.",items[0].Location);
                    if(items.Length<3)throw DeclarationError("AI3106","Expected an alias target.",items[1].Location);
                    targetAt=items[2].Location;
                }
                else if(items[0].Text=="static")throw DeclarationError("AI3100","Static imports require static member import binding.",items[0].Location);
                var target=string.Concat(items.Skip(name is null?0:2).Select(t=>t.Text));
                if(target.Length==0)throw DeclarationError("AI3106","Expected an import target.",targetAt);
                scope.Directives.Add(new(name,target,global,name is null?token.Location:items[0].Location,targetAt));
            }
            catch(ScalarError failure){error??=failure.Diagnostic;}
            if(complete)i=last;else while(i+1<tokens.Count&&tokens[i+1].Location.Offset<end)i++;
        }
        return scope;
    }
    private static string MaskSourceImports(string source)
    {
        if(sourceImports is not {Ranges.Count:>0})return source;var text=source.ToCharArray();
        foreach(var (start,end) in sourceImports.Ranges)for(var i=start;i<end;i++)if(text[i] is not ('\r' or '\n'))text[i]=' ';
        return new(text);
    }
    private static void ValidateSourceImports()
    {
        if(sourceImports is null)return;
        var binder=new ExtendedBinder(activeFeatures,"",new(1,1,0));
        foreach(var entry in sourceImports.Directives)
        {
            if(entry.Name is null)
            {if(!KnownImportNamespace(entry.Target)||activeStructs!.Any(s=>s.Name==entry.Target&&!s.IsTypeParameter)||activeEnums!.Any(e=>e.Name==entry.Target))throw DeclarationError("AI3106",$"Unresolved imported namespace '{entry.Target}'.",entry.TargetLocation);continue;}
            if(activeStructs!.Any(s=>s.Name==entry.Name&&!s.IsTypeParameter)||activeEnums!.Any(e=>e.Name==entry.Name))throw DeclarationError("AI3106",$"Import alias '{entry.Name}' conflicts with a source type.",entry.Location);
            if(KnownImportNamespace(entry.Target)&&!activeStructs!.Any(s=>s.Name==IdentifierName(entry.Target))&&!activeEnums!.Any(e=>e.Name==IdentifierName(entry.Target)))continue;
            _=binder.ImportAliasType(entry.Name,entry.Location,declaration:true);
        }
    }
    private static string ImportSignatureSyntax(string text,IReadOnlySet<string>? shadowed=null)
    {
        if(sourceImports is null||sourceImports.Directives.Count==0)return text;
        var scope=sourceImports;
        string TargetSyntax(SourceImport entry)
        {
            var target=entry.Target.Trim();var compact=Regex.Replace(target,@"\s+","");
            var head=compact.Split('<','[')[0];var leaf=head.StartsWith("global::",StringComparison.Ordinal)?head[8..]:head;
            if(scope.Module.Length>0&&leaf.StartsWith(scope.Module+".",StringComparison.Ordinal))
                return compact[(head.Length-leaf.Length+scope.Module.Length+1)..];
            if(QualifiedBuiltinAlias(compact) is {} builtin)return builtin+(compact.Contains('<')?compact[compact.IndexOf('<')..]:compact.Contains('[')?compact[compact.IndexOf('[')..]:"");
            return target;
        }
        return RewriteTypeIdentifiers(text,name=>
        {
            if(shadowed?.Contains(name)==true)return null;
            var entry=scope.Directives.FirstOrDefault(d=>d.Name==name);if(entry is null)return null;
            return TargetSyntax(entry);
        });
    }
    private sealed partial class ExtendedBinder
    {
        private SourceImport? ImportAlias(string text)=>CanBindTypeParameter(text)?sourceImports?.Directives.FirstOrDefault(d=>d.Name==IdentifierName(text)):null;
        public ScalarType? ImportAliasType(string text,SourceLocation at,bool declaration=false)
        {
            var entry=declaration?sourceImports?.Directives.FirstOrDefault(d=>d.Name==text):ImportAlias(text);if(entry is null)return null;
            if(sourceImports!.Resolving!=0)Fail("AI3106","Alias targets cannot depend on peer or circular using aliases.",at);
            if(sourceImports.Types.TryGetValue(entry.Name!,out var existing))return existing;
            if(KnownImportNamespace(entry.Target)&&!_structs.Any(s=>s.Name==IdentifierName(entry.Target))&&!_enums.Any(e=>e.Name==IdentifierName(entry.Target)))return null;
            sourceImports.Resolving++;
            try
            {
                var binder=new ExtendedBinder(activeFeatures,"",entry.TargetLocation);var type=binder.Type(entry.Target,entry.TargetLocation,allowUnit:true,allowStatic:true);
                if(type==ScalarType.Unit)Fail("AI3106","A type alias cannot target void/unit.",entry.TargetLocation);
                sourceImports.Types[entry.Name!]=type;return type;
            }
            finally{sourceImports.Resolving--;}
        }
        private ScalarType? ImportValueType(string text,SourceLocation at)
        {
            if(IsLocalName(text)||ScopedTypeArgument(text,out _)||_struct is not null&&NameMemberExists(_struct.Id,IdentifierName(text))||_functions.Any(f=>f.Name==IdentifierName(text)))return null;
            return ImportAliasType(text,at);
        }
        private ScalarStruct? ImportedStructure(string text)
        {
            if(sourceImports is null||sourceImports.Resolving!=0||ScopedTypeArgument(text,out _))return null;
            var type=ImportAliasType(text,Current.Location);
            return type is {} value&&IsStruct(value)?StructOf(value):null;
        }
        private ScalarEnum? ImportedEnumeration(string text)
        {
            if(sourceImports is null||sourceImports.Resolving!=0||ScopedTypeArgument(text,out _))return null;
            var type=ImportAliasType(text,Current.Location);
            return type is {} value&&IsEnum(value)?EnumOf(value):null;
        }
        private bool ImportedNamespaceAlias(string name,out string target,SourceLocation? at=null)
        {
            target="";var entry=ImportAlias(name);if(entry is null)return false;
            if(sourceImports!.Resolving!=0)Fail("AI3106","Alias targets cannot depend on peer or circular using aliases.",at??Current.Location);
            if(!KnownImportNamespace(entry.Target)||_structs.Any(s=>s.Name==IdentifierName(entry.Target))||_enums.Any(e=>e.Name==IdentifierName(entry.Target)))return false;
            target=CanonicalPath(entry.Target);if(target.StartsWith("global::",StringComparison.Ordinal))target=target[8..];return true;
        }
        private string ImportQualifiedText(string text,SourceLocation at)
        {
            if(sourceImports is null||(ActiveFeatures&ScalarFeatures.Imports)==0)return text;
            var compact=Regex.Replace(text,@"\s+","");var global=compact.StartsWith("global::",StringComparison.Ordinal);var raw=global?compact[8..]:compact;
            var split=raw.IndexOfAny(['.',':']);
            var arguments=raw.IndexOfAny(['<','[']);if(arguments>=0&&split>arguments)split=-1;
            if(!global&&split>0)
            {
                var head=raw[..split];var forced=raw.AsSpan(split).StartsWith("::");
                if(forced||!ScopedTypeArgument(head,out _))
                {
                    if(ImportedNamespaceAlias(head,out var target,at))raw=target+"."+raw[(split+(forced?2:1))..];
                    else if(forced)Fail("AI3106","The namespace alias qualifier requires a namespace alias.",at);
                }
            }
            var module=sourceImports.Module;
            if(module.Length>0&&raw.StartsWith(module+".",StringComparison.Ordinal))return raw[(module.Length+1)..];
            return raw!=compact||global?"global::"+raw:text;
        }
        private ScalarType? ImportedSimpleType(string text,SourceLocation at,bool allowUnit)
        {
            if(sourceImports is null||sourceImports.Resolving!=0||!IdentifierSpelling.Valid(text))return null;
            var imports=sourceImports.Directives.Where(d=>d.Name is null).Select(d=>CanonicalPath(d.Target)).Select(n=>n.StartsWith("global::",StringComparison.Ordinal)?n[8..]:n).Distinct(StringComparer.Ordinal);
            var candidates=imports.Select(n=>n+"."+IdentifierName(text)).Where(n=>QualifiedBuiltinAlias(n) is not null).Distinct(StringComparer.Ordinal).ToArray();
            if(candidates.Length>1)Fail("AI3106",$"Ambiguous imported type '{text}'.",at);
            return candidates.Length==0?null:Type("global::"+candidates[0],at,allowUnit);
        }

        private (string Name,string Alias,int End)? ImportedQualification(int position,bool expression)
        {
            if(sourceImports is null||(ActiveFeatures&ScalarFeatures.Imports)==0)return null;
            var start=position;var first=TokenAt(position);var head=first.Name;var raw=first.Text;var forced=TokenAt(position+1).Text=="::"||TokenAt(position+1).Text==":"&&TokenAt(position+2).Text==":";
            var global=first.Text=="global"&&forced;
            if(!global&&!forced&&expression&&(IsLocalName(head)||_struct is not null&&NameMemberExists(_struct.Id,head)||_functions.Any(f=>f.Name==head)))return null;
            if(!global&&!forced&&ScopedTypeArgument(first.Text,out _))return null;
            var namespaceAlias=!global&&ImportedNamespaceAlias(first.Text,out _,first.Location);
            if(forced&&!global&&!namespaceAlias&&ImportAlias(first.Text) is not null)Fail("AI3106","The namespace alias qualifier requires a namespace alias.",first.Location);
            if(!global&&!namespaceAlias&&!sourceImports.Module.StartsWith(head+".",StringComparison.Ordinal)&&sourceImports.Module!=head)return null;
            if(forced){position+=TokenAt(position+1).Text=="::"?2:3;raw+="::";if(!IsSourceIdentifier(TokenAt(position).Text))return null;raw+=TokenAt(position++).Text;}
            else position++;
            (string Name,string Alias,int End)? found=null;
            void Consider()
            {
                var expanded=ImportQualifiedText(raw,first.Location);
                if(!global&&!namespaceAlias&&expanded==raw)return;
                var alias=QualifiedBuiltinAlias(expanded);
                if(alias is not null)found=(expanded,alias,position);
                else if(!expanded.Contains('.')&&!expanded.Contains(':')&&(StructNamedAny(expanded) is not null||EnumNamed(expanded) is not null))found=(expanded,expanded,position);
            }
            Consider();
            while(TokenAt(position).Text=="."&&IsSourceIdentifier(TokenAt(position+1).Text))
            {
                raw+="."+TokenAt(position+1).Text;position+=2;Consider();
                if(position-start>64)Fail("AI3100","A qualified import path exceeds 32 components.",first.Location);
            }
            return found;
        }
    }
}
