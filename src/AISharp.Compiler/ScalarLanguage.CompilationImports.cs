namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class CompilationBinder
    {
        private static IEnumerable<Scope> ScopeChain(Scope scope)
        {for(var current=scope;current is not null;current=current.Parent)yield return current;}
        private IEnumerable<Import> ScopeImports(Scope scope)
        {
            foreach(var import in scope.Imports.Where(i=>!i.Global))yield return import;
            if(scope.Parent is null)foreach(var import in globals)yield return import;
        }
        private ScalarStaticImportScope StaticImportScope(Scope scope)
        {
            var directives=ScopeImports(scope).ToArray();var prefix=scope.Name+".";
            var declarations=symbols.Where(s=>!s.Function&&s.Scope.Name==scope.Name&&(s.FileUnit is null||s.FileUnit==scope.Unit)).Select(s=>(s.Name,s.Arity))
                .Concat(directives.Where(i=>i.Name is not null).Select(i=>(i.Name!,0)))
                .Concat(namespaces.Where(n=>n.StartsWith(prefix,StringComparison.Ordinal)&&!n[prefix.Length..].Contains('.')).Select(n=>(n[prefix.Length..],0))).Distinct().ToArray();
            return new(Array.AsReadOnly(directives.Where(i=>i.Static).Select(i=>i.BoundTarget!).Distinct(StringComparer.Ordinal).ToArray()),Array.AsReadOnly(declarations));
        }
        private bool VisibleNestedType(Symbol symbol,Scope scope)
        {
            for(var current=symbol;current.Owner is not null;current=current.Owner)
            {
                if(current.Accessibility is "public" or "internal" or "protected internal")continue;
                if(ScopeChain(scope).Any(s=>s.TypeOwner?.InternalName==current.Owner.InternalName))continue;
                // Protected access is checked against bound inheritance by the scalar binder.
                if(current.Accessibility is "protected" or "private protected")continue;
                return false;
            }
            return true;
        }
        private List<string> ImportedTypes(Unit unit,Scope scope,IEnumerable<Import> imports,string name,int arity,int at,string spelling)
        {
            var candidates=new List<string>();
            foreach(var import in imports.Where(i=>i.Name is null))
            {
                ResolveImport(import);
                if(import.Static)
                {
                    var (owner,captured)=BoundSource(import.BoundTarget!);
                    var nested=owner is null?null:FindVisibleType(unit,s=>s.Owner?.InternalName==owner.InternalName&&s.Name==name&&s.Arity==arity);
                    if(nested is not null&&VisibleNestedType(nested,scope))candidates.Add(TypeReference(unit,nested,spelling,at)+(captured.Length==0?"":"<"+string.Join(",",captured)+">"));
                }
                else
                {
                    var qualified=import.BoundTarget+"."+name;var source=FindVisibleType(unit,s=>s.QualifiedName==qualified&&s.Arity==arity);
                    if(source is not null)candidates.Add(TypeReference(unit,source,spelling,at));else if(QualifiedBuiltinTypes.ContainsKey(qualified))candidates.Add("global::"+qualified);
                }
            }
            return candidates.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
