namespace AISharp.Compiler;

/// <summary>Classifies the kind of a declared symbol.</summary>
public enum SymbolKind
{
    Module,
    Import,
    Policy,
    Capability,
    Resource,
    Effect,
    Type,
    Schema,
    Tool,
    Macro,
    Generator,
    Function,
    Actor,
    ActorState,
    ActorHandler,
    Test,
    Observability
}

/// <summary>Represents a single named declaration in the semantic model.</summary>
/// <param name="Kind">The kind of declaration.</param>
/// <param name="Name">The declared name.</param>
/// <param name="Location">The source location of the declaration.</param>
/// <param name="ActorScope">Non-null when this symbol belongs to an actor scope.</param>
public sealed record Symbol(
    SymbolKind Kind,
    string Name,
    SourceLocation Location,
    string? ActorScope = null);

/// <summary>
/// Flat, queryable symbol table built from a parsed AI# document.
/// Supports document scope and actor member scope.
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<(SymbolKind Kind, string Name), Symbol> _documentSymbols
        = new();

    private readonly HashSet<string> _documentNames
        = new(StringComparer.Ordinal);

    private readonly Dictionary<(string ActorName, string MemberName), Symbol> _actorMembers
        = new();

    internal void AddDocument(Symbol symbol)
    {
        _documentNames.Add(symbol.Name);
        _documentSymbols[(symbol.Kind, symbol.Name)] = symbol;
    }

    internal void AddActorMember(string actorName, Symbol symbol) =>
        _actorMembers[(actorName, symbol.Name)] = symbol;

    /// <summary>Returns the document-scope symbol with the given kind and name, or null.</summary>
    public Symbol? LookupDocument(SymbolKind kind, string name) =>
        _documentSymbols.TryGetValue((kind, name), out var s) ? s : null;

    /// <summary>Returns the first document-scope symbol with the given name across all kinds, or null.</summary>
    public Symbol? LookupDocument(string name) =>
        _documentSymbols.Values.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Returns the actor member (state or handler) with the given name, or null.</summary>
    public Symbol? LookupActorMember(string actorName, string memberName) =>
        _actorMembers.TryGetValue((actorName, memberName), out var s) ? s : null;

    /// <summary>Returns true when a document-scope symbol with the given kind and name exists.</summary>
    public bool ContainsDocument(SymbolKind kind, string name) =>
        _documentSymbols.ContainsKey((kind, name));

    /// <summary>Returns true when any document-scope symbol with the given name exists.</summary>
    public bool ContainsDocumentName(string name) =>
        _documentNames.Contains(name);

    /// <summary>All document-scope symbols in declaration order.</summary>
    public IReadOnlyList<Symbol> AllDocumentSymbols => [.. _documentSymbols.Values];

    /// <summary>All member symbols for the specified actor.</summary>
    public IReadOnlyList<Symbol> GetActorMembers(string actorName) =>
        [.. _actorMembers
            .Where(kv => string.Equals(kv.Key.ActorName, actorName, StringComparison.Ordinal))
            .Select(kv => kv.Value)];
}

/// <summary>
/// A resolved reference from a type annotation in the source to its declared symbol.
/// Carries the raw type text, the source location of the declaration that owns the
/// annotation, the context (e.g. "parameter:api"), and the resolved symbol (null when
/// the name could not be resolved).
/// </summary>
public sealed record TypeReference(
    string TypeText,
    SourceLocation Location,
    string Context,
    Symbol? ResolvedSymbol);

/// <summary>Classifies a resolved non-declaration reference in source.</summary>
public enum SymbolReferenceKind
{
    Type,
    Capability,
    Resource,
    Effect,
    ModelOutputSchema,
    ToolCapability
}

/// <summary>
/// A resolved source reference to a declared symbol. Unresolved references are
/// retained with a null <see cref="ResolvedSymbol"/> so tools can surface them.
/// </summary>
public sealed record SymbolReference(
    string Name,
    SymbolReferenceKind ReferenceKind,
    SourceLocation Location,
    string Context,
    Symbol? ResolvedSymbol);

/// <summary>
/// Result of semantic analysis: carries the original parse result,
/// the constructed symbol table, resolved type references, and any semantic-layer diagnostics.
/// </summary>
public sealed record SemanticResult(
    ParseResult ParseResult,
    SymbolTable SymbolTable,
    IReadOnlyList<Diagnostic> SemanticDiagnostics,
    IReadOnlyList<TypeReference> ResolvedTypeReferences,
    IReadOnlyList<SymbolReference> ResolvedSymbolReferences)
{
    /// <summary>Effect-check result; populated after the effect pass runs.</summary>
    public EffectCheckResult? EffectCheckResult { get; init; }

    /// <summary>Contract-check result; populated after the contract semantics pass runs.</summary>
    public ContractCheckResult? ContractCheckResult { get; init; }

    /// <summary>Backward-compatible constructor without type references (empty list).</summary>
    public SemanticResult(
        ParseResult parseResult,
        SymbolTable symbolTable,
        IReadOnlyList<Diagnostic> semanticDiagnostics)
        : this(parseResult, symbolTable, semanticDiagnostics, [], []) { }

    /// <summary>Backward-compatible constructor without symbol references (empty list).</summary>
    public SemanticResult(
        ParseResult parseResult,
        SymbolTable symbolTable,
        IReadOnlyList<Diagnostic> semanticDiagnostics,
        IReadOnlyList<TypeReference> resolvedTypeReferences)
        : this(parseResult, symbolTable, semanticDiagnostics, resolvedTypeReferences, []) { }

    /// <summary>All diagnostics from both parser and semantic analysis.</summary>
    public IReadOnlyList<Diagnostic> AllDiagnostics =>
        [.. ParseResult.Diagnostics, .. SemanticDiagnostics];

    /// <summary>True when no error-severity diagnostics exist in either layer.</summary>
    public bool Success =>
        AllDiagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
}

/// <summary>
/// Builds a semantic model (symbol table) from a parsed AI# document.
/// Detects duplicate declarations at document scope (AI3001) and actor scope (AI3002).
/// </summary>
public static class AISharpSemanticModel
{
    /// <summary>
    /// Builds the symbol table from <paramref name="parseResult"/> and returns a
    /// <see cref="SemanticResult"/> that combines the parse result, symbol table,
    /// and any semantic-layer diagnostics.
    /// </summary>
    public static SemanticResult Build(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var table = new SymbolTable();
        var diagnostics = new List<Diagnostic>();
        var doc = parseResult.Document;

        // Module
        if (doc.ModuleDeclaration is not null)
        {
            RegisterDocument(table, diagnostics, SymbolKind.Module, doc.ModuleDeclaration.Name, doc.ModuleDeclaration.Location);
        }
        else if (doc.Module is not null)
        {
            RegisterDocument(table, diagnostics, SymbolKind.Module, doc.Module, new SourceLocation(1, 1, 0));
        }

        // Use imports
        foreach (var use in doc.Uses)
            RegisterDocument(table, diagnostics, SymbolKind.Import, use.Path, use.Location);

        // Policies
        foreach (var p in doc.Policies)
            RegisterDocument(table, diagnostics, SymbolKind.Policy, p.Name, p.Location);

        // Capabilities
        foreach (var c in doc.Capabilities)
            RegisterDocument(table, diagnostics, SymbolKind.Capability, c.Name, c.Location);

        // Resources
        foreach (var r in doc.Resources)
            RegisterDocument(table, diagnostics, SymbolKind.Resource, r.Name, r.Location);

        // Effects
        foreach (var effect in doc.EffectDeclarations)
            RegisterDocument(table, diagnostics, SymbolKind.Effect, effect.Name, effect.Location);

        if (doc.EffectDeclarations.Count == 0)
        {
            foreach (var effect in doc.Effects)
            {
                RegisterDocument(table, diagnostics, SymbolKind.Effect, effect, new SourceLocation(1, 1, 0));
            }
        }

        // Types (union and record)
        foreach (var t in doc.Types)
            RegisterDocument(table, diagnostics, SymbolKind.Type, t.Name, t.Location);

        // Schemas
        foreach (var s in doc.Schemas)
            RegisterDocument(table, diagnostics, SymbolKind.Schema, s.Name, s.Location);

        // Tools
        foreach (var tool in doc.Tools)
            RegisterDocument(table, diagnostics, SymbolKind.Tool, tool.Name, tool.Location);

        // Macros
        foreach (var m in doc.Macros)
            RegisterDocument(table, diagnostics, SymbolKind.Macro, m.Name, m.Location);

        // Generators
        foreach (var g in doc.Generators)
            RegisterDocument(table, diagnostics, SymbolKind.Generator, g.Name, g.Location);

        // Functions
        foreach (var f in doc.Functions)
            RegisterDocument(table, diagnostics, SymbolKind.Function, f.Name, f.Location);

        // Actors (document scope for actor name; actor scope for members)
        foreach (var actor in doc.Actors)
        {
            RegisterDocument(table, diagnostics, SymbolKind.Actor, actor.Name, actor.Location);
            RegisterActorMembers(table, diagnostics, actor);
        }

        // Tests
        foreach (var test in doc.Tests)
            RegisterDocument(table, diagnostics, SymbolKind.Test, test.Name, test.Location);

        // Observability
        foreach (var obs in doc.Observability)
            RegisterDocument(table, diagnostics, SymbolKind.Observability, obs.Name, obs.Location);

        var resolution = ResolveNames(doc, table, diagnostics);
        diagnostics.AddRange(AISharpTypeChecker.Check(doc, table));

        var effectResult = AISharpEffectChecker.Check(doc, table);
        diagnostics.AddRange(effectResult.Diagnostics);

        var contractResult = AISharpContractChecker.Check(doc, table);
        diagnostics.AddRange(contractResult.Diagnostics);

        diagnostics.AddRange(AISharpResourceLifetimeAnalyzer.Check(doc));

        return new SemanticResult(parseResult, table, diagnostics, resolution.TypeReferences, resolution.SymbolReferences)
        {
            EffectCheckResult = effectResult,
            ContractCheckResult = contractResult
        };
    }

    private static void RegisterDocument(
        SymbolTable table,
        List<Diagnostic> diagnostics,
        SymbolKind kind,
        string name,
        SourceLocation location)
    {
        if (table.ContainsDocumentName(name))
        {
            diagnostics.Add(new Diagnostic(
                "AI3001",
                DiagnosticSeverity.Error,
                $"'{name}' is declared more than once in document scope.",
                location));
        }
        else
        {
            table.AddDocument(new Symbol(kind, name, location));
        }
    }

    private static void RegisterActorMembers(
        SymbolTable table,
        List<Diagnostic> diagnostics,
        ActorDeclaration actor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var state in actor.StateMembers)
        {
            if (!seen.Add(state.Name))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3002",
                    DiagnosticSeverity.Error,
                    $"Actor '{actor.Name}' member '{state.Name}' is declared more than once.",
                    state.Location));
            }
            else
            {
                table.AddActorMember(actor.Name,
                    new Symbol(SymbolKind.ActorState, state.Name, state.Location, actor.Name));
            }
        }

        foreach (var handler in actor.Handlers)
        {
            if (!seen.Add(handler.Name))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3002",
                    DiagnosticSeverity.Error,
                    $"Actor '{actor.Name}' member '{handler.Name}' is declared more than once.",
                    handler.Location));
            }
            else
            {
                table.AddActorMember(actor.Name,
                    new Symbol(SymbolKind.ActorHandler, handler.Name, handler.Location, actor.Name));
            }
        }
    }

    // ──────────────────────── Name Resolution ────────────────────────

    // AI# first-class built-in effect names.
    private static readonly HashSet<string> BuiltInEffects = new(StringComparer.Ordinal)
    {
        "io", "time", "random", "model", "network", "unsafe_ffi"
    };

    // AI# built-in primitive type names.
    private static readonly HashSet<string> BuiltInPrimitives = new(StringComparer.Ordinal)
    {
        "string", "int", "bool", "float", "bytes", "unit", "void",
        "byte", "sbyte", "short", "ushort", "uint", "long", "ulong", "double", "decimal",
        "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64",
        "f32", "f64", "char", "any", "never", "unknown", "Error",
        "CustomerId", "ApiKey", "OrderId"  // treated as opaque value types for now
    };

    // Well-known generic carrier names whose head resolves without a declaration.
    private static readonly HashSet<string> WellKnownGenericCarriers = new(StringComparer.Ordinal)
    {
        "Result", "Option", "List", "Map", "Set", "Tuple",
        "Stream", "Future", "Task", "Array", "Dict", "Pair",
        "Either", "Vec"
    };

    /// <summary>
    /// Resolves all type annotations and named semantic references in <paramref name="doc"/> against
    /// <paramref name="table"/> and appends any AI3010 diagnostics to
    /// <paramref name="diagnostics"/>.
    /// </summary>
    private static NameResolutionResult ResolveNames(
        AISharpDocument doc,
        SymbolTable table,
        List<Diagnostic> diagnostics)
    {
        // When the document has use-imports we cannot be certain which names they
        // export, so we only produce errors for names that are definitely local.
        bool hasUseImports = doc.Uses.Count > 0;
        var typeRefs = new List<TypeReference>();
        var symbolRefs = new List<SymbolReference>();

        // Function parameters and return types.
        foreach (var fn in doc.Functions)
        {
            ResolveParameterTypes(fn.Parameters, fn.Location, $"fn:{fn.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
            ResolveTypeAnnotation(fn.ReturnType, fn.Location, $"return:{fn.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
            ResolveFunctionReferences(fn, table, symbolRefs);
        }

        // Actor handlers (treated as functions).
        foreach (var actor in doc.Actors)
        {
            // Actor state fields.
            foreach (var state in actor.StateMembers)
                ResolveTypeAnnotation(state.Type, state.Location, $"actor-state:{actor.Name}.{state.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);

            foreach (var handler in actor.Handlers)
            {
                ResolveParameterTypes(handler.Parameters, handler.Location, $"actor-handler:{actor.Name}.{handler.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
                ResolveTypeAnnotation(handler.ReturnType, handler.Location, $"return:{actor.Name}.{handler.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
                ResolveFunctionReferences(handler, table, symbolRefs);
            }
        }

        // Schema fields.
        foreach (var schema in doc.Schemas)
        {
            foreach (var field in schema.Fields)
                ResolveTypeAnnotation(field.Type, schema.Location, $"schema:{schema.Name}.{field.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
        }

        // Record type fields.
        foreach (var type in doc.Types)
        {
            foreach (var field in type.Fields)
                ResolveTypeAnnotation(field.Type, type.Location, $"record:{type.Name}.{field.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
        }

        // Tool parameters and return type.
        foreach (var tool in doc.Tools)
        {
            ResolveParameterTypes(tool.Parameters, tool.Location, $"tool:{tool.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
            ResolveTypeAnnotation(tool.ReturnType, tool.Location, $"return:{tool.Name}", table, diagnostics, typeRefs, symbolRefs, hasUseImports);
            foreach (var capability in tool.Capabilities)
            {
                symbolRefs.Add(new SymbolReference(
                    capability,
                    SymbolReferenceKind.ToolCapability,
                    tool.Location,
                    $"tool-capability:{tool.Name}",
                    table.LookupDocument(SymbolKind.Capability, capability)));
            }
        }

        return new NameResolutionResult(typeRefs, symbolRefs);
    }

    private static void ResolveParameterTypes(
        IReadOnlyList<ParameterDeclaration> parameters,
        SourceLocation location,
        string ownerContext,
        SymbolTable table,
        List<Diagnostic> diagnostics,
        List<TypeReference> refs,
        List<SymbolReference> symbolRefs,
        bool hasUseImports)
    {
        foreach (var param in parameters)
            ResolveTypeAnnotation(param.Type, location, $"parameter:{param.Name}", table, diagnostics, refs, symbolRefs, hasUseImports);
    }

    private static void ResolveTypeAnnotation(
        string typeText,
        SourceLocation location,
        string context,
        SymbolTable table,
        List<Diagnostic> diagnostics,
        List<TypeReference> refs,
        List<SymbolReference> symbolRefs,
        bool hasUseImports)
    {
        if (string.IsNullOrWhiteSpace(typeText))
            return;

        var names = ExtractTypeNames(typeText);
        if (names.Count == 0)
            return;

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var resolved = TryResolveTypeName(name, table);
            refs.Add(new TypeReference(i == 0 ? typeText.Trim() : name, location, context, resolved));

            if (resolved is not null)
            {
                var referenceKind = resolved.Kind switch
                {
                    SymbolKind.Capability => SymbolReferenceKind.Capability,
                    SymbolKind.Resource => SymbolReferenceKind.Resource,
                    _ => SymbolReferenceKind.Type
                };
                symbolRefs.Add(new SymbolReference(name, referenceKind, location, context, resolved));
            }

            if (resolved is null && !hasUseImports && !IsKnownName(name) && !IsGenericParameterPlaceholder(name))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3010",
                    DiagnosticSeverity.Error,
                    $"Type name '{name}' could not be resolved. Declare it or import it via a 'use' declaration.",
                    location));
            }
        }
    }

    private static void ResolveFunctionReferences(
        FunctionDeclaration function,
        SymbolTable table,
        List<SymbolReference> symbolRefs)
    {
        foreach (var effect in function.Effects)
        {
            var resolved = table.LookupDocument(SymbolKind.Effect, effect)
                ?? (BuiltInEffects.Contains(effect)
                    ? new Symbol(SymbolKind.Effect, effect, new SourceLocation(0, 0, 0))
                    : null);
            symbolRefs.Add(new SymbolReference(
                effect,
                SymbolReferenceKind.Effect,
                function.Location,
                $"effect:{function.Name}",
                resolved));
        }

        foreach (var modelCall in function.ModelCalls)
        {
            if (!string.IsNullOrWhiteSpace(modelCall.OutputSchema))
            {
                symbolRefs.Add(new SymbolReference(
                    modelCall.OutputSchema,
                    SymbolReferenceKind.ModelOutputSchema,
                    modelCall.OutputSchemaReference?.Location ?? modelCall.Location,
                    $"model-output:{function.Name}",
                    table.LookupDocument(SymbolKind.Schema, modelCall.OutputSchema)));
            }
        }
    }

    private static Symbol? TryResolveTypeName(string name, SymbolTable table)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        return table.LookupDocument(SymbolKind.Type, name)
            ?? table.LookupDocument(SymbolKind.Capability, name)
            ?? table.LookupDocument(SymbolKind.Resource, name)
            ?? table.LookupDocument(SymbolKind.Schema, name);
    }

    private static bool IsKnownName(string name) =>
        BuiltInPrimitives.Contains(name) || WellKnownGenericCarriers.Contains(name);

    /// <summary>
    /// Extracts the head (base) type name from a potentially generic or qualified type
    /// string.  Examples:
    ///   "Result&lt;T, E&gt;" → "Result"
    ///   "List&lt;Order&gt;"  → "List"
    ///   "string"           → "string"
    ///   "CustomerId"       → "CustomerId"
    /// </summary>
    internal static string ExtractHeadTypeName(string typeText)
    {
        var trimmed = typeText.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        // Take up to the first '<' or whitespace.
        var end = trimmed.IndexOfAny(['<', ' ', '\t', '\r', '\n']);
        var head = end < 0 ? trimmed : trimmed[..end];
        return head.Trim();
    }

    private static IReadOnlyList<string> ExtractTypeNames(string typeText)
    {
        var names = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var ch in typeText)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                current.Append(ch);
                continue;
            }

            FlushCurrent();
        }

        FlushCurrent();
        return names;

        void FlushCurrent()
        {
            if (current.Length == 0)
                return;

            names.Add(current.ToString());
            current.Clear();
        }
    }

    private static bool IsGenericParameterPlaceholder(string name) =>
        name.Length <= 2 && name.All(char.IsUpper);

    private sealed record NameResolutionResult(
        IReadOnlyList<TypeReference> TypeReferences,
        IReadOnlyList<SymbolReference> SymbolReferences);
}
