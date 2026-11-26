using System.Security.Cryptography;
using System.Text;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static void ValidateCompilationFileExposure(IReadOnlyList<FunctionDeclaration> functions)
    {
        if(activeCompilation is null||!activeCompilation.Bindings.Any(b=>b.FileIdentity is not null))return;
        bool FileOwner(string name)=>CompilationBinding(name)?.FileIdentity is not null;
        void Signature(string syntax,SourceLocation at)
        {
            if(new Lexer(syntax).Lex().Any(t=>t.Kind!=TokenKind.String&&FileOwner(t.Text)))
                throw DeclarationError("AI3119","A non-file-local declaration cannot expose a file-local type in its signature.",at);
        }
        void Type(ScalarType type,SourceLocation at,HashSet<ScalarType> visited)
        {
            if(!visited.Add(type))return;
            var owner=(int)type>=StructTypeBase&&(int)type<StructTypeBase+activeStructs!.Count?activeStructs[(int)type-StructTypeBase]:null;
            var enumeration=(int)type>=EnumTypeBase&&(int)type<EnumTypeBase+activeEnums!.Count?activeEnums[(int)type-EnumTypeBase]:null;
            if(FileOwner(owner?.Name??enumeration?.Name??""))
                throw DeclarationError("AI3119","A non-file-local declaration cannot expose a file-local type in its signature.",at);
            if(owner?.ArrayElementType is {} element)Type(element,at,visited);
            if(owner is not null)foreach(var argument in owner.TypeArgumentList)Type(argument,at,visited);
            if(enumeration is not null)
            {
                void Shape(ScalarRuntimeTypeShape shape)
                {if(shape.NamedType is {} named)Type(named,at,visited);if(shape.Element is {} child)Shape(child);if(shape.Arguments is {} arguments)foreach(var argument in arguments)Shape(argument);}
                foreach(var argument in enumeration.TypeArguments)Shape(argument);
            }
        }
        void Method(FunctionDeclaration method)
        {
            Signature(method.ReturnType,method.Location);
            foreach(var parameter in method.Parameters)Signature(parameter.Type,method.Location);
            if(ExtendedBinder.GenericOf(method) is {} generic)foreach(var parameter in generic.Parameters)foreach(var constraint in parameter.Constraints)Signature(constraint.Text,constraint.Location);
        }
        foreach(var owner in activeStructs!.ToArray())
        {
            if(CompilationBinding(owner.Name) is null||FileOwner(owner.Name)||owner.IsTypeParameter)continue;var index=IndexOf(owner.Id);
            if(owner.BaseIndex>=0)Type(activeStructs[owner.BaseIndex].Id,owner.Location,[]);
            // Implementing a file-local interface is permitted. Its constructed arguments still form part of this signature.
            foreach(var implemented in owner.Interfaces)foreach(var argument in activeStructs[implemented].TypeArgumentList)Type(argument,owner.Location,[]);
            foreach(var field in owner.Fields.Concat(owner.StaticFields).Where(f=>f.Owner==index&&!f.Hidden))Type(field.Type,field.Location,[]);
            foreach(var property in owner.Properties.Where(p=>p.Owner==index&&!p.IsSynthesizedRecordTypeContract))
            {Type(property.Type,property.Location,[]);foreach(var parameter in property.IndexParameters)Signature(parameter.Type,property.Location);}
            foreach(var method in owner.Methods.Where(m=>m.Owner==index&&!m.IsAccessor&&!m.IsLifted))Method(method.Declaration);
            if(typeGenerics!.TryGetValue(index,out var definition))foreach(var parameter in definition.Parameters)foreach(var constraint in parameter.Constraints)Signature(constraint.Text,constraint.Location);
        }
        foreach(var function in functions)Method(function);
    }
    private sealed partial class CompilationBinder
    {
        public static bool ContainsFileDeclarations(string source)
        {
            if(!source.Contains("file",StringComparison.Ordinal))return false;
            var tokens=CompilationTokens(source);
            for(var i=0;i<tokens.Count-1;i++)
            {
                if(tokens[i].Kind==TokenKind.String||tokens[i].Text!="file")continue;
                var next=i+1;while(next<tokens.Count-1&&(tokens[next].Kind==TokenKind.NewLine||Modifiers.Contains(tokens[next].Text)))next++;
                if(TypeWords.Contains(tokens[next].Text))return true;
            }
            return false;
        }
        private static string FileIdentity(Unit unit)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unit.Input.Path))).ToLowerInvariant();
        private string FileRuntimeName(Symbol symbol)
            =>symbol.Owner is {} owner?FileRuntimeName(owner)+(owner.Arity==0?"":"`"+owner.Arity)+"+"+symbol.Name:symbol.SourceNamespace+".<file_"+FileIdentity(symbol.FileUnit!)+">__"+symbol.Name;
        private void FinalizeFileTypeIdentities()
        {
            if(!hasFileDeclarations)return;
            foreach(var local in symbols.Where(s=>s.FileLocal&&s.Partial&&s.Owner is null).ToArray())
                foreach(var part in symbols.Where(s=>s.Partial&&s.Owner is null&&s.Unit==local.Unit&&s.QualifiedName==local.QualifiedName&&s.Arity==local.Arity))part.FileLocal=true;
            foreach(var group in symbols.Where(s=>!s.Function).GroupBy(s=>(s.FullName,s.Arity,File:s.FileUnit)))
            {
                var parts=group.ToArray();var first=parts[0];
                if(parts.Length>1&&parts.Any(p=>!p.Partial))Fail(parts[^1].Unit,parts[^1].Offset,(first.Owner is null?"Duplicate declaration '"+parts[^1].FullName+"'.":"Duplicate nested type '"+first.Name+"'.")+(parts.Any(p=>p.Partial)?" Every type part must be partial.":""));
                if(first.FileUnit is not null&&parts.Any(p=>p.DeclarationModifiers.Any(m=>m is "public" or "private" or "protected" or "internal")&&p.Owner is null))Fail(first.Unit,first.Offset,"A file-local type cannot declare accessibility modifiers.");
                var name=first.FileUnit is {} file?"__unit_t_file_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Input.Path+"\0"+first.FullName+"\0"+first.Arity))).ToLowerInvariant():"__unit_t_"+symbols.IndexOf(first);
                foreach(var part in parts)part.InternalName=name;
            }
        }
        private Symbol? FindVisibleType(Unit unit,Func<Symbol,bool> predicate,Scope? scope=null)
        {
            bool Context(Symbol symbol)=>scope is not null&&symbol.Owner is not null&&ScopeChain(scope).Any(s=>s.TypeOwner?.InternalName==symbol.Owner.InternalName);
            return symbols.Where(s=>!s.Function&&(s.FileUnit is null||s.FileUnit==unit)&&predicate(s))
                .OrderByDescending(s=>s.FileUnit==unit).ThenByDescending(Context).ThenBy(s=>scope is null?0:Captures(s).Count).FirstOrDefault();
        }
        private void CheckFileTypeUse(Unit unit,string syntax,int at)
        {
            var tokens=new Lexer(syntax).Lex();
            foreach(var token in tokens)
                if(symbols.FirstOrDefault(s=>!s.Function&&s.InternalName==token.Text&&s.FileUnit is not null) is {} type&&type.FileUnit!=unit)
                    Fail(unit,at,"A file-local type is not visible in this source unit.");
        }
        private bool ContainsFileType(string syntax)=>new Lexer(syntax).Lex().Any(token=>symbols.Any(s=>!s.Function&&s.InternalName==token.Text&&s.FileUnit is not null));
    }
}
