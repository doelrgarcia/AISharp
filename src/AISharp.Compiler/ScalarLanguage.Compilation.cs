using System.Security.Cryptography;
using System.Text;

namespace AISharp.Compiler;

/// <summary>An in-memory source unit. Path is a diagnostic identity; compilation never reads it.</summary>
public sealed record ScalarSourceUnit(string Path, string Source);
public sealed record ScalarUnitDiagnostic(string Path, Diagnostic Diagnostic, bool IsGenerated = false);
internal sealed record ScalarStaticImportScope(IReadOnlyList<string> Targets,IReadOnlyList<(string Name,int Arity)> Declarations);
public sealed record ScalarCompilationBinding(string InternalName, string Namespace, string Name, int Arity, bool IsFunction)
{
    public string? DeclaringType { get; init; }
    public string? FullName { get; init; }
    public string Accessibility { get; init; } = "internal";
    public int CapturedArity { get; init; }
    public IReadOnlyList<string> TypeParameters { get; init; } = [];
    public string? FileIdentity { get; init; }
    public string? RuntimeFullName { get; init; }
}

/// <summary>A namespace-bound source group and its mapping back to the original units.</summary>
public sealed class ScalarCompilation
{
    internal sealed record Segment(int Start, int End, int Unit, int OriginalStart, int OriginalLength);
    internal List<Segment> Segments { get; } = [];
    internal sealed record NamespaceSpan(int Unit,int Start,int End,string Name);
    internal IReadOnlyList<NamespaceSpan> Namespaces { get; set; } = [];
    internal Func<string,int,int,string?>? TypeResolver { get; set; }
    internal Func<string,int,string?>? TypeNormalizer { get; set; }
    internal Func<string,int,(string Target,bool Namespace)?>? AliasResolver { get; set; }
    internal Func<string,int,string?>? NamespaceResolver { get; set; }
    internal Func<string,int,string?>? FunctionResolver { get; set; }
    internal Func<int,IReadOnlyList<ScalarStaticImportScope>>? StaticImportResolver { get; set; }
    internal Func<int,string?>? TypeOwnerResolver { get; set; }
    internal Func<int,IReadOnlyList<string>?>? PartialParameterResolver { get; set; }
    internal Func<int,IReadOnlyList<string>?>? PartialTypeParameterResolver { get; set; }
    internal Func<int,bool>? OmittedPartialResolver { get; set; }
    internal bool HasOmittedPartialMembers { get; set; }
    internal IReadOnlyDictionary<string,string> CapturedParameterNames { get; set; } = new Dictionary<string,string>();
    internal bool IsGeneratedOffset(int offset)=>!Segments.Any(s=>s.Start<=offset&&offset<s.End&&s.OriginalLength>0);
    internal string? NamespaceAt(int offset)
    {
        var segment=Segments.LastOrDefault(s=>s.Start<=offset&&offset<s.End);if(segment is null)return null;
        var at=segment.OriginalStart+Math.Min(Math.Max(0,offset-segment.Start),Math.Max(0,segment.OriginalLength-1));
        return Namespaces.Where(s=>s.Unit==segment.Unit&&s.Start<=at&&at<s.End).OrderByDescending(s=>s.Start).ThenBy(s=>s.End).FirstOrDefault()?.Name;
    }
    public IReadOnlyList<ScalarSourceUnit> Units { get; internal init; } = [];
    public IReadOnlyList<ScalarCompilationBinding> Bindings { get; internal set; } = [];
    public IReadOnlyList<ScalarUnitDiagnostic> Diagnostics { get; internal set; } = [];
    public string EntryModule { get; internal set; } = "";
    public string EntryNamespace { get; internal set; } = "";
    public string Source { get; internal set; } = "";
    public string SourceSha256 { get; internal set; } = "";
    public ScalarFeatures RequiredFeatures { get; internal set; } = ScalarFeatures.CompilationUnits;
    public bool Success => Diagnostics.All(d => d.Diagnostic.Severity != DiagnosticSeverity.Error);
    public ScalarUnitDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        var at = diagnostic.Location.Offset;
        var segment = Segments.LastOrDefault(s => s.Start <= at && at < s.End);
        if (segment is null) return new(Units.Count == 0 ? "" : Units[0].Path, diagnostic, true);
        var unit = Units[segment.Unit];
        var offset = segment.OriginalStart + Math.Min(Math.Max(0, at - segment.Start), Math.Max(0, segment.OriginalLength - 1));
        var prefix = unit.Source[..offset];
        var message=diagnostic.Message;
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var binding in Bindings.Where(b=>b.InternalName!=b.Name))names[binding.InternalName]=binding.FullName?.Replace('+','.')??binding.Namespace+"."+binding.Name;
        foreach(var parameter in CapturedParameterNames)names[parameter.Key]=parameter.Value;
        if(names.Count>0)
        {
            const string identifierPart=@"[\w\p{Mc}\p{Cf}\uD800-\uDFFF]";
            var pattern="(?<!"+identifierPart+")(?:"+string.Join("|",names.Keys.OrderByDescending(n=>n.Length).Select(System.Text.RegularExpressions.Regex.Escape))+")(?!"+identifierPart+")";
            message=System.Text.RegularExpressions.Regex.Replace(message,pattern,m=>names[m.Value],System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        return new(unit.Path, diagnostic with { Message=message, Location = new(prefix.Count(c => c == '\n') + 1, offset - prefix.LastIndexOf('\n'), offset) },segment.OriginalLength==0);
    }
}

public static partial class ScalarLanguage
{
    /// <summary>Bind a group of source units together before the scalar/native passes.</summary>
    public static ScalarCompilation BindCompilation(IReadOnlyList<ScalarSourceUnit> units, string entryModule)
        => new CompilationBinder(units, entryModule).Bind();
    public static ScalarCompilation BindCompilation(string source, string path = "<input>") => CompilationBinder.Single(source,path);
    public static bool ContainsNamespaceDeclarations(string source) => CompilationBinder.ContainsNamespaces(source);
    public static bool RequiresCompilationBinding(string source) => CompilationBinder.ContainsNamespaces(source)||CompilationBinder.ContainsStaticImports(source)||CompilationBinder.ContainsNestedTypes(source)||CompilationBinder.ContainsPartialDeclarations(source)||CompilationBinder.ContainsFileDeclarations(source);

    private sealed partial class CompilationBinder
    {
        private sealed class Unit
        {
            public Unit(ScalarSourceUnit source,int index){Input=source;Index=index;Tokens=CompilationTokens(source.Source,out var diagnostics);LexDiagnostics=diagnostics;}
            public ScalarSourceUnit Input;
            public int Index;
            public IReadOnlyList<Token> Tokens;
            public IReadOnlyList<Diagnostic> LexDiagnostics;
            public string Module = "";
            public List<Scope> Scopes = [];
            public List<Edit> Edits = [];
            public List<Shadow> Shadows = [];
            public bool HeaderRead;
            public bool IntentRead;
            public bool FileNamespace;
        }
        private sealed class Scope(Unit unit, string name, int start, int end, Scope? parent)
        {
            public Unit Unit = unit;
            public string Name = name;
            public int Start = start;
            public int End = end;
            public Scope? Parent = parent;
            public List<Import> Imports = [];
            public bool Members;
            public Symbol? TypeOwner;
        }
        private sealed class Import(string? name, string target, bool global, Scope scope, int offset, bool isStatic=false)
        {
            public string? Name = name;
            public string Target = target;
            public bool Global = global;
            public Scope Scope = scope;
            public int Offset = offset;
            public bool Resolving;
            public string? BoundTarget;
            public bool NamespaceTarget;
            public bool Static = isStatic;
        }
        private sealed record Symbol(Unit Unit, Scope Scope, string Name, int Arity, bool Function, int Offset, int Start, int End, string InternalName)
        {
            public string InternalName { get; set; } = InternalName;
            public string QualifiedName => Scope.Name + "." + Name;
            public Symbol? Owner { get; init; }
            public string Accessibility { get; set; } = "internal";
            public string Kind { get; init; } = "";
            public bool Partial { get; init; }
            public bool FileLocal { get; set; }
            public Unit? FileUnit => Owner?.FileUnit??(FileLocal?Unit:null);
            public IReadOnlyList<string> DeclarationModifiers { get; init; } = [];
            public string SourceNamespace => Owner?.SourceNamespace??Scope.Name;
            public string FullName => Owner is null?QualifiedName:Owner.FullName+(Owner.Arity==0?"":"`"+Owner.Arity)+"+"+Name;
        }
        private sealed record Edit(int Start, int End, string Text, Symbol? DeclarationOwner = null);
        private sealed record Shadow(int Start, int End, string Name, bool Type,string? Replacement=null);
        private sealed class BindingFailure(Unit unit, int offset, string message, string code = "AI3106") : Exception(message)
        { public Unit Unit = unit; public int Offset = offset; public string Code = code; }
        private readonly ScalarCompilation result;
        private readonly List<Unit> units = [];
        private readonly List<Symbol> symbols = [];
        private readonly List<Import> globals = [];
        private readonly HashSet<string> namespaces = new(StringComparer.Ordinal);
        private string entry;
        private readonly bool inferEntry;
        private Symbol? entrySymbol;
        private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal) { "class", "struct", "interface", "record", "enum", "delegate" };
        private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal) { "public", "internal", "private", "protected", "abstract", "sealed", "static", "readonly", "partial", "async", "file" };
        private readonly bool hasFileDeclarations;
        public CompilationBinder(IReadOnlyList<ScalarSourceUnit> inputs, string entryModule,bool inferEntry=false)
        {
            ArgumentNullException.ThrowIfNull(inputs); ArgumentNullException.ThrowIfNull(entryModule);
            entry = Canonical(entryModule); this.inferEntry=inferEntry;result = new() { Units = Array.AsReadOnly(inputs.ToArray()), EntryModule = entry,EntryNamespace=entry };
            if (inputs.Count == 0) throw new ArgumentException("A compilation needs at least one source unit.", nameof(inputs));
            if (inputs.Any(u => u is null || u.Path is null || u.Source is null)) throw new ArgumentException("Source units, paths and text cannot be null.", nameof(inputs));
            if (inputs.Select(u => u.Path).Distinct(StringComparer.Ordinal).Count() != inputs.Count) throw new ArgumentException("Diagnostic source paths must be unique.", nameof(inputs));
            for (var i = 0; i < inputs.Count; i++) units.Add(new(inputs[i], i));
            hasFileDeclarations=inputs.Any(u=>ContainsFileDeclarations(u.Source));
        }
        private static string Name(string raw) => IdentifierSpelling.Valid(raw) ? IdentifierSpelling.Name(raw) : raw;
        private static string Canonical(string raw) => string.Join(".", raw.Replace("global::", "", StringComparison.Ordinal).Split('.').Select(p => Name(p.Trim())));
        private static bool Identifier(Token token) => token.Kind!=TokenKind.String&&IdentifierSpelling.Valid(token.Text);
        private static bool ModuleIdentifier(Token token) => token.Kind==TokenKind.Identifier;
        private static bool SourceIdentifier(Token token) => Identifier(token)&&(token.Text[0]=='@'||Name(token.Text)!=token.Text||!ReservedTypeParameterNames.Contains(token.Text));
        private static bool DeclarationIdentifier(Token token)=>SourceIdentifier(token)&&(token.Text[0]=='@'||Name(token.Text)!=token.Text||!BuiltInTypeNames.Contains(token.Text,StringComparer.Ordinal)&&token.Text is not ("let" or "var"));
        private static int Offset(Unit unit, int index) => unit.Tokens[Math.Min(index, unit.Tokens.Count - 1)].Location.Offset;
        private static int Next(Unit unit, int index) { while (index < unit.Tokens.Count - 1 && unit.Tokens[index].Kind == TokenKind.NewLine) index++; return index; }
        private static string TokenText(Unit unit, int start, int end) => string.Concat(unit.Tokens.Skip(start).Take(end - start).Where(t => t.Kind != TokenKind.NewLine).Select(t => t.Text));
        private void Fail(Unit unit, int offset, string message, string code = "AI3106") => throw new BindingFailure(unit, offset, message, code);
        private void Namespace(string name)
        {
            while (name.Length != 0) { namespaces.Add(name); var dot = name.LastIndexOf('.'); name = dot < 0 ? "" : name[..dot]; }
        }
        private static string Mask(string text) => new(text.Select(c => c is '\n' or '\r' ? c : ' ').ToArray());
        private void Mask(Unit unit, int start, int end) => unit.Edits.Add(new(start, end, Mask(unit.Input.Source[start..end])));
        private int Match(Unit unit, int start, string open, string close)
        {
            var depth = 0;
            for (var i = start; i < unit.Tokens.Count; i++) { if(unit.Tokens[i].Kind==TokenKind.String)continue;if (unit.Tokens[i].Text == open) depth++; else if (unit.Tokens[i].Text == close && --depth == 0) return i; }
            Fail(unit, Offset(unit, start), "Unterminated '" + open + "'."); return start;
        }
        public ScalarCompilation Bind()
        {
            try
            {
                foreach(var unit in units)foreach(var token in unit.Tokens.Where(Identifier))
                {
                    var name=Name(token.Text);
                    string[] reserved=["__unit_","__native_box_","__native_object_","__native_array_","__native_enumerator_","__native_collection_","__native_equatable","__native_comparer_","__native_sequence_shape_","__native_type_","__Grp_","__GrpN_","__grp_","__query_pair_","__query_create_","__IEnumerable_"+NativeSequenceShapePrefix,"__IEnumerator_"+NativeSequenceShapePrefix];
                    if(reserved.Any(prefix=>name.StartsWith(prefix,StringComparison.Ordinal)))Fail(unit,token.Location.Offset,"This identifier prefix is reserved for compiler metadata and storage.");
                }
                foreach (var unit in units) ReadUnit(unit);
                FinalizeFileTypeIdentities();
                foreach(var unit in units)foreach(var token in unit.Tokens.Where(Identifier))
                {
                    if(token.Text[0]=='@')result.RequiredFeatures|=ScalarFeatures.EscapedIdentifiers;
                    if(token.Text.Contains('\\')||token.Text.Any(c=>c>127))result.RequiredFeatures|=ScalarFeatures.UnicodeIdentifiers;
                }
                foreach (var symbol in symbols.Where(s => !s.Function&&s.Arity==0&&s.FileUnit is null))
                    if (namespaces.Contains(symbol.QualifiedName)) Fail(symbol.Unit, symbol.Offset, "A namespace and type cannot share '" + symbol.QualifiedName + "'.");
                var entries = symbols.Where(s => s.Function && s.QualifiedName == entry + ".main").ToArray();
                if(entries.Length==0&&inferEntry)entries=symbols.Where(s=>s.Function&&s.Name=="main").ToArray();
                if (entries.Length != 1) Fail(units[0], 0, "The selected entry module must contain exactly one fn main.");
                entrySymbol = entries[0];
                entry=entrySymbol.Scope.Name;result.EntryNamespace=entry;result.EntryModule=entrySymbol.Unit.Module;
                foreach (var import in globals)
                    if (globals.Any(i => !ReferenceEquals(i, import) && import.Name is not null && i.Name == import.Name)) Fail(import.Scope.Unit, import.Offset, "Duplicate global import alias '" + import.Name + "'.");
                foreach (var import in units.SelectMany(u => u.Scopes).SelectMany(s => s.Imports)) ResolveImport(import);
                foreach(var scope in units.SelectMany(u=>u.Scopes))foreach(var import in scope.Imports.Where(i=>i.Name is not null&&!i.Global))
                    if(symbols.Any(s=>!s.Function&&s.Scope.Name==scope.Name&&s.Name==import.Name))Fail(scope.Unit,import.Offset,"An import alias conflicts with a source type in this namespace.");
                foreach (var unit in units) ReadShadows(unit);
                ValidatePartialTypes();
                ValidatePartialMembers();
                foreach (var unit in units) Rewrite(unit);
                result.Namespaces=Array.AsReadOnly(units.SelectMany(u=>u.Scopes.Select(s=>new ScalarCompilation.NamespaceSpan(u.Index,s.Start,s.End,s.Name))).ToArray());
                BuildSource();
            }
            catch (BindingFailure failure)
            {
                var text = failure.Unit.Input.Source; var at = Math.Clamp(failure.Offset, 0, text.Length); var prefix = text[..at];
                result.Diagnostics = [new(failure.Unit.Input.Path, new(failure.Code, DiagnosticSeverity.Error, failure.Message, new(prefix.Count(c => c == '\n') + 1, at - prefix.LastIndexOf('\n'), at)))];
            }
            result.Bindings = Array.AsReadOnly(symbols.DistinctBy(s=>s.InternalName).Select(s => new ScalarCompilationBinding(ReferenceEquals(s, entrySymbol) ? "main" : s.InternalName, s.SourceNamespace, s.Name, s.Arity, s.Function){DeclaringType=s.Owner?.InternalName,FullName=s.FullName,Accessibility=s.Accessibility,CapturedArity=Captures(s).Count,TypeParameters=Array.AsReadOnly(Captures(s).Select(p=>Name(p.Original)).Concat(Parameters(s).Select(Name)).ToArray()),FileIdentity=s.FileUnit is {} file?FileIdentity(file):null,RuntimeFullName=s.FileUnit is not null?FileRuntimeName(s):null}).ToArray());
            result.CapturedParameterNames = new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(symbols.SelectMany(Captures).DistinctBy(c=>c.Flat).ToDictionary(c=>c.Flat,c=>Name(c.Original),StringComparer.Ordinal));
            var identity = System.Text.Json.JsonSerializer.Serialize(new { entry, units = result.Units.Select(u => new { u.Path, u.Source }) });
            result.SourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            return result;
        }
        private void ReadUnit(Unit unit)
        {
            if(unit.LexDiagnostics.FirstOrDefault(d=>d.Severity==DiagnosticSeverity.Error) is {} lexical)Fail(unit,lexical.Location.Offset,lexical.Message,lexical.Code);
            var t = unit.Tokens; var module = t.ToList().FindIndex(x => x.Kind == TokenKind.Module);
            if (module < 0||Next(unit,0)!=module) Fail(unit, 0, "A source unit must begin with its module declaration.");
            var end = module + 1; while (end < t.Count && t[end].Kind is not (TokenKind.NewLine or TokenKind.EndOfFile)) end++;
            var path = t.Skip(module + 1).Take(end - module - 1).ToArray();
            if (path.Length == 0 || path.Where((_, i) => i % 2 == 0).Any(x => !ModuleIdentifier(x)) || path.Where((_, i) => i % 2 == 1).Any(x => x.Text != ".") || path.Length % 2 == 0) Fail(unit, Offset(unit, module), "A module requires its existing identifier path syntax.");
            unit.Module = Canonical(TokenText(unit, module + 1, end)); Namespace(unit.Module);
            var root = new Scope(unit, unit.Module, 0, unit.Input.Source.Length, null); unit.Scopes.Add(root);
            ScanScope(unit, 0, t.Count - 1, root);
        }
        private void ScanScope(Unit unit, int start, int end, Scope scope)
        {
            var t = unit.Tokens; var regular = false;
            for (var i = start; i < end;)
            {
                i = Next(unit, i); if (i >= end) break; var token = t[i];
                if (token.Kind is TokenKind.Module or TokenKind.Intent)
                {
                    if (scope.Parent is not null || scope.Members) Fail(unit, token.Location.Offset, "Module and intent headers belong at the start of their source unit.");
                    var stop = i + 1; while (stop < end && t[stop].Kind != TokenKind.NewLine) stop++;
                    if(token.Kind==TokenKind.Module){if(unit.HeaderRead)Fail(unit,token.Location.Offset,"A source unit has one module declaration.");unit.HeaderRead=true;}
                    else {if(unit.IntentRead||stop!=i+2||t[i+1].Kind!=TokenKind.String||unit.Input.Source[t[i+1].Location.Offset]!='"')Fail(unit,token.Location.Offset,"An intent header needs one string and cannot be repeated.");unit.IntentRead=true;}
                    Mask(unit, token.Location.Offset, Offset(unit, stop)); i = stop; continue;
                }
                if (token.Text == ";") { i++; continue; }
                if (token.Text == "namespace")
                {
                    if(unit.FileNamespace)Fail(unit,token.Location.Offset,"A file-scoped namespace cannot be mixed with namespace blocks or another file-scoped namespace.");
                    scope.Members = true; var nameStart = Next(unit, i + 1); var stop = nameStart;
                    while (stop < end && t[stop].Text is not ("{" or ";")) stop++;
                    var parts = t.Skip(nameStart).Take(stop - nameStart).Where(x => x.Kind != TokenKind.NewLine).ToArray();
                    if (parts.Length == 0 || parts.Length % 2 == 0 || parts.Where((_, n) => n % 2 == 0).Any(x => !SourceIdentifier(x)) || parts.Where((_, n) => n % 2 == 1).Any(x => x.Text != ".")) Fail(unit, token.Location.Offset, "Invalid namespace path.");
                    var name = scope.Name + "." + Canonical(TokenText(unit, nameStart, stop)); Namespace(name);
                    if (stop >= end) Fail(unit, token.Location.Offset, "A namespace requires a body or ';'.");
                    if (t[stop].Text == ";")
                    {
                        if (scope.Parent is not null || unit.Scopes.Count != 1 || symbols.Any(s => s.Unit == unit)) Fail(unit, token.Location.Offset, "A file-scoped namespace must precede declarations and cannot be mixed with namespace blocks.");
                        unit.FileNamespace=true;Mask(unit, token.Location.Offset, Offset(unit, stop) + 1); var nested = new Scope(unit, name, Offset(unit, stop) + 1, scope.End, scope); unit.Scopes.Add(nested); ScanScope(unit, stop + 1, end, nested); return;
                    }
                    var close = Match(unit, stop, "{", "}");
                    Mask(unit, token.Location.Offset, Offset(unit, stop) + 1); Mask(unit, Offset(unit, close), Offset(unit, close) + 1);
                    var child = new Scope(unit, name, Offset(unit, stop) + 1, Offset(unit, close), scope); unit.Scopes.Add(child); ScanScope(unit, stop + 1, close, child); i = close + 1; continue;
                }
                var following = Next(unit, i + 1); var global = token.Text == "global" && t[following].Text == "using";
                if (token.Text == "using" || global)
                {
                    result.RequiredFeatures|=ScalarFeatures.Imports;
                    if (scope.Members) Fail(unit, token.Location.Offset, "Imports must precede declarations in their namespace body.");
                    if (global && (scope.Parent is not null || regular)) Fail(unit, token.Location.Offset, "Global imports belong before ordinary imports at compilation-unit scope.");
                    regular |= !global; var first = Next(unit, global ? following + 1 : i + 1); var stop = first;
                    while (stop < end && t[stop].Text != ";" && t[stop].Text is not ("{" or "}")) stop++;
                    if (stop >= end || t[stop].Text != ";") Fail(unit, token.Location.Offset, "An import directive must end with ';'.");
                    var isStatic=t[first].Text=="static";
                    if(isStatic){result.RequiredFeatures|=ScalarFeatures.StaticImports;first=Next(unit,first+1);}
                    string? alias = null; var target = first; var equals = Next(unit, first + 1);
                    if (t[equals].Text == "=")
                    {
                        if (!Identifier(t[first]) || t[first].Text[0] != '@' && Name(t[first].Text) == t[first].Text && ReservedTypeParameterNames.Contains(t[first].Text)) Fail(unit, Offset(unit, first), "Invalid import alias name.");
                        alias = Name(t[first].Text); target = Next(unit, equals + 1);
                        if (scope.Imports.Any(x => x.Name == alias)) Fail(unit, Offset(unit, first), "Duplicate import alias '" + alias + "'.");
                    }
                    if (target >= stop) Fail(unit, token.Location.Offset, "Expected an import target.");
                    if(isStatic&&alias is not null)Fail(unit,Offset(unit,first),"A static import cannot declare an alias.");
                    var import = new Import(alias, TokenText(unit, target, stop), global, scope, Offset(unit, target),isStatic); scope.Imports.Add(import); if (global) globals.Add(import);
                    Mask(unit, token.Location.Offset, Offset(unit, stop) + 1); i = stop + 1; continue;
                }
                var declaration = i; while (declaration < end && Modifiers.Contains(t[declaration].Text)) declaration = Next(unit, declaration + 1);
                var kind = t[declaration].Text;
                if (!TypeWords.Contains(kind) && kind != "fn") Fail(unit, token.Location.Offset, "Expected a type, function or namespace declaration.");
                scope.Members = true; var nameIndex = Next(unit, declaration + 1);
                if (kind == "record" && t[nameIndex].Text is "class" or "struct") nameIndex = Next(unit, nameIndex + 1);
                if (kind == "delegate" && t[nameIndex].Text == "fn") nameIndex = Next(unit, nameIndex + 1);
                if (!DeclarationIdentifier(t[nameIndex])) Fail(unit, Offset(unit, nameIndex), "Invalid declaration name.");
                var declaredName = Name(t[nameIndex].Text); var cursor = Next(unit, nameIndex + 1); var arity = 0;
                if (t[cursor].Text == "<")
                {
                    var close = Match(unit, cursor, "<", ">"); arity = 1 + t.Skip(cursor + 1).Take(close - cursor - 1).Count(x => x.Text == ","); cursor = Next(unit, close + 1);
                }
                var stopIndex = cursor;
                while (stopIndex < end && (t[stopIndex].Kind==TokenKind.String || t[stopIndex].Text != "{" && t[stopIndex].Text != ";"))
                {
                    if (kind is "record" or "delegate" && t[stopIndex].Kind == TokenKind.NewLine) break;
                    stopIndex++;
                }
                int declarationStop;
                if (stopIndex < end && t[stopIndex].Text == "{") { var close = Match(unit, stopIndex, "{", "}"); declarationStop = Offset(unit, close) + 1; cursor = close + 1; }
                else if (kind is "record" or "delegate") { declarationStop = Offset(unit, stopIndex) + (t[stopIndex].Text == ";" ? 1 : 0); cursor = stopIndex + 1; }
                else { Fail(unit, Offset(unit, declaration), "A type or function requires a body."); return; }
                var declarationModifiers=t.Skip(i).Take(declaration-i).Where(x=>x.Kind!=TokenKind.NewLine).Select(x=>x.Text).ToArray();
                var previousDeclaration=symbols.FirstOrDefault(s=>s.QualifiedName==scope.Name+"."+declaredName&&(s.Function&&kind=="fn"||!s.Function&&kind!="fn"&&s.Arity==arity));
                if(hasFileDeclarations&&kind!="fn")previousDeclaration=null;
                var isPartial=declarationModifiers.Contains("partial");
                var isFile=declarationModifiers.Contains("file");
                if(isFile&&(kind=="fn"||declarationModifiers.Any(m=>m is "public" or "private" or "protected" or "internal")))Fail(unit,Offset(unit,declaration),"A file-local type cannot declare an accessibility modifier or be a function.");
                if(isPartial&&(kind=="fn"||kind is "enum" or "delegate"))Fail(unit,Offset(unit,declaration),"This declaration kind cannot be partial.");
                if(previousDeclaration is not null&&(!isPartial||!previousDeclaration.Partial))Fail(unit,Offset(unit,nameIndex),"Duplicate declaration '"+scope.Name+"."+declaredName+"'."+(isPartial||previousDeclaration.Partial?" Every type part must be partial.":""));
                if (unit.Tokens.FirstOrDefault(x => x.Kind!=TokenKind.String&&x.Location.Offset >= token.Location.Offset && x.Location.Offset < declarationStop && x.Text == "namespace") is {} illegalNamespace) Fail(unit, illegalNamespace.Location.Offset, "A namespace cannot be declared inside a type or function.");
                var symbol = new Symbol(unit, scope, declaredName, arity, kind == "fn", Offset(unit, nameIndex), token.Location.Offset, declarationStop,previousDeclaration?.InternalName??"__unit_" + (kind == "fn" ? "f_" : "t_") + symbols.Count){Kind=kind,Partial=isPartial,FileLocal=isFile,DeclarationModifiers=declarationModifiers}; symbols.Add(symbol);
                if(isPartial)result.RequiredFeatures|=ScalarFeatures.PartialTypes;
                if(isFile)result.RequiredFeatures|=ScalarFeatures.FileLocalTypes;
                if(!symbol.Function&&stopIndex<end&&t[stopIndex].Text=="{")ReadNestedTypes(unit,symbol,stopIndex,Match(unit,stopIndex,"{","}"));
                i = cursor;
            }
        }
    }
}
