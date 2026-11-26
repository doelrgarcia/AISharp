using System.Text;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static ScalarCompilation? activeCompilation;
    public static ParseResult ParseCompilation(ScalarCompilation compilation, ScalarFeatures features, int pointerBits,
        out IReadOnlyList<ScalarEnum> enums, out IReadOnlyList<ScalarStruct> structs, out ScalarNativeIntegerInfo target)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if(pointerBits is not (32 or 64))throw new ArgumentOutOfRangeException(nameof(pointerBits));
        if (!compilation.Success) throw new ArgumentException("Namespace binding must succeed before scalar binding.", nameof(compilation));
        if((features&compilation.RequiredFeatures)!=compilation.RequiredFeatures)
        {
            enums=[];structs=[];target=new(pointerBits,false);
            var empty=AISharpParser.Parse("module "+compilation.EntryModule+"\nintent \"Disabled compilation group\"\n");
            return empty with {Diagnostics=[new("AI3100",DiagnosticSeverity.Error,"Compilation-group syntax requires its explicit namespace/import/identifier feature flags.",new(1,1,0))]};
        }
        var previous = activeCompilation; activeCompilation = compilation;
        try { var parsed=ParseExtended(compilation.Source, features, pointerBits, out enums, out structs, out target);return parsed with{Document=parsed.Document with{Module=compilation.EntryModule}}; }
        finally { activeCompilation = previous; }
    }
    private static ScalarCompilationBinding? CompilationBinding(string name) => activeCompilation?.Bindings.FirstOrDefault(b => b.InternalName == name.Split('<')[0]);
    private static string CompilationDisplayName(string name) => activeCompilation is null ? name : RewriteTypeIdentifiers(name, n => activeCompilation.Bindings.FirstOrDefault(b => b.InternalName == n)?.Name??activeCompilation.CapturedParameterNames.GetValueOrDefault(n));
    private static string? CompilationFunctionName(string name,SourceLocation at,string? fallback=null)
    {
        if(activeCompilation is null||!CanBindTypeParameter(name))return null;var space=activeCompilation.NamespaceAt(at.Offset)??fallback;if(space is null)return null;
        var canonical=IdentifierSpelling.Valid(name)?IdentifierSpelling.Name(name):name;
        while(true)
        {
            var binding=activeCompilation.Bindings.FirstOrDefault(b=>b.IsFunction&&b.Namespace==space&&b.Name==canonical);if(binding is not null)return binding.InternalName;
            var dot=space.LastIndexOf('.');if(dot<0)break;space=space[..dot];
        }
        return null;
    }
    private sealed partial class ExtendedBinder
    {
        private string? ScopedCompilationFunction(string name,SourceLocation at)=>CompilationFunctionName(name,at,_struct?.SourceNamespace);
    }
    private sealed partial class CompilationBinder
    {
        public static ScalarCompilation Single(string source,string path)
        {
            ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(path);
            var tokens=CompilationTokens(source);var start=tokens.FindIndex(t=>t.Kind==TokenKind.Module);var end=start+1;
            while(end<tokens.Count&&tokens[end].Kind is not (TokenKind.NewLine or TokenKind.EndOfFile))end++;
            var module=start<0?"compilation.invalid":Canonical(string.Concat(tokens.Skip(start+1).Take(end-start-1).Select(t=>t.Text)));
            return new CompilationBinder([new(path,source)],module,inferEntry:true).Bind();
        }
        public static bool ContainsNamespaces(string source)
        {
            ArgumentNullException.ThrowIfNull(source);if(!source.Contains("namespace",StringComparison.Ordinal))return false;var depth=0;var header=false;
            foreach(var token in CompilationTokens(source))
            {if(token.Kind==TokenKind.NewLine){header=false;continue;}if(depth==0&&token.Kind is TokenKind.Module or TokenKind.Intent){header=true;continue;}if(header||token.Kind==TokenKind.String)continue;if(token.Text=="namespace"&&depth==0)return true;if(token.Text=="{")depth++;else if(token.Text=="}")depth--;}
            return false;
        }
        public static bool ContainsStaticImports(string source)
        {
            ArgumentNullException.ThrowIfNull(source);if(!source.Contains("using",StringComparison.Ordinal))return false;
            var tokens=CompilationTokens(source);var depth=0;
            for(var i=0;i<tokens.Count-1;i++)
            {
                var token=tokens[i];if(token.Kind==TokenKind.String)continue;
                if(token.Text=="{")depth++;else if(token.Text=="}")depth--;
                if(depth==0&&token.Text=="using"&&tokens[NextToken(i+1)].Text=="static")return true;
            }
            return false;
            int NextToken(int at){while(at<tokens.Count-1&&tokens[at].Kind==TokenKind.NewLine)at++;return at;}
        }
        private static List<Token> CompilationTokens(string source)=>CompilationTokens(source,out _);
        private static List<Token> CompilationTokens(string source,out IReadOnlyList<Diagnostic> diagnostics)
        {
            var masked = source.ToCharArray();var characters=new List<(int Start,int End)>();var errors=new List<Diagnostic>();
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '"') { for (i++; i < source.Length; i++) { if (source[i] == '\\') i++; else if (source[i] == '"') break; } continue; }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/') { while (i < source.Length && source[i] is not ('\r' or '\n')) i++; continue; }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    var begin = i; i += 2; while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    var closed=i+1<source.Length;i=closed?i+1:source.Length-1;
                    if(!closed)errors.Add(new("AI0001",DiagnosticSeverity.Error,"Unterminated block comment.",LocationAt(source,begin)));
                    for (var k = begin; k <= i; k++) if (masked[k] is not ('\r' or '\n')) masked[k] = ' '; continue;
                }
                if (source[i] != '\'') continue;
                var start = i; for (i++; i < source.Length && source[i] is not ('\r' or '\n'); i++) { if (source[i] == '\\') i++; else if (source[i] == '\'') break; }
                characters.Add((start,Math.Min(i+1,source.Length)));
                for (var k = start; k <= Math.Min(i, source.Length - 1); k++) if (masked[k] is not ('\r' or '\n')) masked[k] = ' ';
            }
            var lexer=new Lexer(new string(masked));var tokens=lexer.Lex();errors.AddRange(lexer.Diagnostics);diagnostics=errors.AsReadOnly();
            foreach(var (start,end) in characters){var prefix=source[..start];tokens.Add(new(TokenKind.String,source[start..end],new(prefix.Count(c=>c=='\n')+1,start-prefix.LastIndexOf('\n'),start)));}
            return tokens.OrderBy(t=>t.Location.Offset).ThenBy(t=>t.Kind==TokenKind.EndOfFile?1:0).ToList();
        }
    }
}
