namespace AISharp.Compiler;

// ─── Stable identifier ────────────────────────────────────────────────────────

/// <summary>
/// A stable, deterministic identifier for a HIR node derived from its declaration path.
/// Format: <c>fn:getActiveOrders</c>, <c>actor:OrderActor:handler:onOrder</c>, etc.
/// </summary>
public sealed record HirId(string Value)
{
    public static HirId ForDocument(string? module) =>
        new($"doc:{module ?? "(anonymous)"}");

    public static HirId ForFunction(string name) => new($"fn:{name}");

    public static HirId ForActorHandler(string actorName, string handlerName) =>
        new($"actor:{actorName}:handler:{handlerName}");

    public static HirId ForActor(string name) => new($"actor:{name}");

    public static HirId ForType(string name) => new($"type:{name}");

    public static HirId ForSchema(string name) => new($"schema:{name}");

    public static HirId ForTool(string name) => new($"tool:{name}");

    public static HirId ForTest(string name) => new($"test:{name}");

    public override string ToString() => Value;
}

// ─── HIR parameter / contract / statement nodes ───────────────────────────────

/// <summary>Typed parameter in a function, tool, or schema.</summary>
public sealed record HirParameter(
    string Name,
    AISharpType Type,
    ParameterOwnership Ownership,
    SourceLocation Location);

/// <summary>Contract (requires / ensures / invariant) attached to a function.</summary>
public sealed record HirContract(
    ContractKind Kind,
    string Expression,
    SourceLocation Location);

/// <summary>Abstract base for statements in a HIR function body.</summary>
public abstract record HirStatement(SourceLocation Location);

/// <summary><c>let name: Type = expr</c> statement.</summary>
public sealed record HirLetStatement(
    string Name,
    AISharpType? AnnotationType,
    string ExpressionText,
    AISharpType InferredType,
    SourceLocation Location) : HirStatement(Location);

/// <summary><c>return expr</c> statement.</summary>
public sealed record HirReturnStatement(
    string ExpressionText,
    AISharpType InferredType,
    SourceLocation Location) : HirStatement(Location);

/// <summary>A bare expression statement.</summary>
public sealed record HirExpressionStatement(
    string ExpressionText,
    SourceLocation Location) : HirStatement(Location);

/// <summary>A parallel task group (<c>task { … }</c>).</summary>
public sealed record HirTaskGroupStatement(
    string? Name,
    IReadOnlyList<HirStatement> Statements,
    SourceLocation Location) : HirStatement(Location);

/// <summary>A lexical resource lifetime block (<c>using resource { ... }</c>).</summary>
public sealed record HirUsingStatement(
    string ResourceName,
    IReadOnlyList<HirStatement> Statements,
    SourceLocation Location) : HirStatement(Location);

/// <summary>A spawn statement.</summary>
public sealed record HirSpawnStatement(
    string Name,
    string ExpressionText,
    SourceLocation Location) : HirStatement(Location);

/// <summary>An <c>all [a, b, c]</c> sync point.</summary>
public sealed record HirAllStatement(
    IReadOnlyList<string> Targets,
    SourceLocation Location) : HirStatement(Location);

/// <summary>A single arm in a match statement.</summary>
public sealed record HirMatchArm(string Pattern, string BodyText, SourceLocation Location);

/// <summary>A <c>match scrutinee { Pattern => expr }</c> statement.</summary>
public sealed record HirMatchStatement(
    string Scrutinee,
    IReadOnlyList<HirMatchArm> Arms,
    SourceLocation Location) : HirStatement(Location);

// ─── HIR declaration nodes ────────────────────────────────────────────────────

/// <summary>Abstract base for all named HIR declarations.</summary>
public abstract record HirNode(HirId Id, SourceLocation Location);

/// <summary>Model call attached to a function.</summary>
public sealed record HirModelCall(
    string Provider,
    string Model,
    string? Task,
    string? Input,
    string? OutputSchema,
    AISharpType? OutputType,
    ModelReplayDeclaration? Replay,
    bool CapturesProvenance,
    SchemaReferenceDeclaration? OutputSchemaReference,
    ProvenanceMetadataDeclaration? Provenance,
    SourceLocation Location);

/// <summary>A function (or actor handler) in the HIR.</summary>
public sealed record HirFunction(
    HirId Id,
    string Name,
    IReadOnlyList<HirParameter> Parameters,
    AISharpType ReturnType,
    IReadOnlyList<string> Effects,
    IReadOnlyList<HirContract> Contracts,
    IReadOnlyList<HirStatement> Body,
    IReadOnlyList<HirModelCall> ModelCalls,
    Symbol? Symbol,
    bool IsAsync,
    SourceLocation Location) : HirNode(Id, Location);

/// <summary>State field of an actor.</summary>
public sealed record HirStateField(
    string Name,
    AISharpType Type,
    SourceLocation Location);

/// <summary>An actor declaration in the HIR.</summary>
public sealed record HirActor(
    HirId Id,
    string Name,
    IReadOnlyList<HirStateField> StateMembers,
    IReadOnlyList<HirFunction> Handlers,
    Symbol? Symbol,
    SourceLocation Location) : HirNode(Id, Location);

/// <summary>A type declaration (union or record) in the HIR.</summary>
public sealed record HirTypeDecl(
    HirId Id,
    string Name,
    TypeKind Kind,
    IReadOnlyList<string> Cases,
    IReadOnlyList<HirParameter> Fields,
    Symbol? Symbol,
    SourceLocation Location) : HirNode(Id, Location);

/// <summary>A schema declaration in the HIR.</summary>
public sealed record HirSchema(
    HirId Id,
    string Name,
    IReadOnlyList<HirParameter> Fields,
    Symbol? Symbol,
    SourceLocation Location) : HirNode(Id, Location);

/// <summary>A tool declaration in the HIR.</summary>
public sealed record HirTool(
    HirId Id,
    string Name,
    IReadOnlyList<HirParameter> Parameters,
    AISharpType ReturnType,
    string? Sandbox,
    IReadOnlyList<string> Capabilities,
    string? Timeout,
    Symbol? Symbol,
    SourceLocation Location) : HirNode(Id, Location);

/// <summary>An inline test declaration in the HIR.</summary>
public sealed record HirTest(
    HirId Id,
    string Name,
    SourceLocation Location) : HirNode(Id, Location);

/// <summary>Root HIR document node containing all top-level declarations.</summary>
public sealed record HirDocument(
    HirId Id,
    string? Module,
    string? Intent,
    IReadOnlyList<HirTypeDecl> Types,
    IReadOnlyList<HirSchema> Schemas,
    IReadOnlyList<HirTool> Tools,
    IReadOnlyList<HirFunction> Functions,
    IReadOnlyList<HirActor> Actors,
    IReadOnlyList<HirTest> Tests,
    SourceLocation Location) : HirNode(Id, Location), IHirArtifact
{
    public IReadOnlyList<ScalarFunction> ScalarFunctions { get; init; } = [];
    public bool ScalarValidationFailed { get; init; }
}

// ─── HIR build result ─────────────────────────────────────────────────────────

/// <summary>
/// Result of the HIR build pass.  Contains the <see cref="HirDocument"/> and
/// a flat source-map dictionary from stable <see cref="HirId"/> strings to their
/// corresponding <see cref="SourceLocation"/>, plus the resolved semantic
/// references that were used to normalize the HIR.
/// </summary>
public sealed record HirBuildResult(
    HirDocument Document,
    IReadOnlyDictionary<string, SourceLocation> SourceMap,
    IReadOnlyList<TypeReference> ResolvedTypeReferences,
    IReadOnlyList<SymbolReference> ResolvedSymbolReferences);

// ─── HIR builder ─────────────────────────────────────────────────────────────

/// <summary>
/// Builds a typed, stable-ID High-Level Intermediate Representation from a
/// <see cref="SemanticResult"/>.
/// </summary>
public static class AISharpHirBuilder
{
    /// <summary>
    /// Normalizes the semantic result into a <see cref="HirDocument"/> with stable
    /// declaration IDs, resolved types, and a source-map from IDs to locations.
    /// </summary>
    public static HirBuildResult Build(SemanticResult semanticResult)
    {
        ArgumentNullException.ThrowIfNull(semanticResult);

        var doc   = semanticResult.ParseResult.Document;
        var table = semanticResult.SymbolTable;
        var sourceMap = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);

        var docLoc = new SourceLocation(1, 1, 0);
        var docId  = HirId.ForDocument(doc.Module);
        sourceMap[docId.Value] = docLoc;

        var hirTypes    = doc.Types.Select(t   => BuildType(t, table, sourceMap)).ToList();
        var hirSchemas  = doc.Schemas.Select(s => BuildSchema(s, table, sourceMap)).ToList();
        var hirTools    = doc.Tools.Select(tool => BuildTool(tool, table, sourceMap)).ToList();
        var hirFunctions = doc.Functions.Select(fn => BuildFunction(fn, null, table, sourceMap)).ToList();
        var hirActors   = doc.Actors.Select(a  => BuildActor(a, table, sourceMap)).ToList();
        var hirTests    = doc.Tests.Select(t => BuildTest(t, sourceMap)).ToList();

        var hirDoc = new HirDocument(
            docId,
            doc.Module,
            doc.Intent,
            hirTypes,
            hirSchemas,
            hirTools,
            hirFunctions,
            hirActors,
            hirTests,
            docLoc) { ScalarFunctions = doc.ScalarFunctions, ScalarValidationFailed = doc.ScalarValidationFailed };

        return new HirBuildResult(
            hirDoc,
            sourceMap,
            semanticResult.ResolvedTypeReferences,
            semanticResult.ResolvedSymbolReferences);
    }

    // ── declaration builders ─────────────────────────────────────────────────

    private static HirFunction BuildFunction(
        FunctionDeclaration fn,
        string? actorName,
        SymbolTable table,
        Dictionary<string, SourceLocation> sourceMap)
    {
        var id = actorName is null
            ? HirId.ForFunction(fn.Name)
            : HirId.ForActorHandler(actorName, fn.Name);

        sourceMap[id.Value] = fn.Location;

        var parameters  = fn.Parameters.Select(p => BuildParameter(p, table)).ToList();
        var returnType  = AISharpTypeChecker.ParseType(fn.ReturnType, table);
        var contracts   = fn.Contracts.Select(c => new HirContract(c.Kind, c.Expression, c.Location)).ToList();
        var body        = fn.Body.Statements.Select(s => LowerStatement(s, table)).ToList();
        var modelCalls  = fn.ModelCalls.Select(mc => BuildModelCall(mc, table)).ToList();
        var symbol      = table.LookupDocument(SymbolKind.Function, fn.Name)
                       ?? (actorName is null ? null : table.LookupActorMember(actorName, fn.Name));

        return new HirFunction(
            id,
            fn.Name,
            parameters,
            returnType,
            fn.Effects,
            contracts,
            body,
            modelCalls,
            symbol,
            fn.IsAsync,
            fn.Location);
    }

    private static HirActor BuildActor(
        ActorDeclaration actor,
        SymbolTable table,
        Dictionary<string, SourceLocation> sourceMap)
    {
        var id = HirId.ForActor(actor.Name);
        sourceMap[id.Value] = actor.Location;

        var stateMembers = actor.StateMembers
            .Select(s => new HirStateField(
                s.Name,
                AISharpTypeChecker.ParseType(s.Type, table),
                s.Location))
            .ToList();

        var handlers = actor.Handlers
            .Select(h => BuildFunction(h, actor.Name, table, sourceMap))
            .ToList();

        var symbol = table.LookupDocument(SymbolKind.Actor, actor.Name);

        return new HirActor(id, actor.Name, stateMembers, handlers, symbol, actor.Location);
    }

    private static HirTypeDecl BuildType(
        TypeDeclaration t,
        SymbolTable table,
        Dictionary<string, SourceLocation> sourceMap)
    {
        var id = HirId.ForType(t.Name);
        sourceMap[id.Value] = t.Location;

        var fields = t.Fields.Select(f => BuildParameter(f, table)).ToList();
        var symbol = table.LookupDocument(SymbolKind.Type, t.Name);

        return new HirTypeDecl(id, t.Name, t.Kind, t.Cases, fields, symbol, t.Location);
    }

    private static HirSchema BuildSchema(
        SchemaDeclaration s,
        SymbolTable table,
        Dictionary<string, SourceLocation> sourceMap)
    {
        var id = HirId.ForSchema(s.Name);
        sourceMap[id.Value] = s.Location;

        var fields = s.Fields.Select(f => BuildParameter(f, table)).ToList();
        var symbol = table.LookupDocument(SymbolKind.Schema, s.Name);

        return new HirSchema(id, s.Name, fields, symbol, s.Location);
    }

    private static HirTool BuildTool(
        ToolDeclaration tool,
        SymbolTable table,
        Dictionary<string, SourceLocation> sourceMap)
    {
        var id = HirId.ForTool(tool.Name);
        sourceMap[id.Value] = tool.Location;

        var parameters = tool.Parameters.Select(p => BuildParameter(p, table)).ToList();
        var returnType = AISharpTypeChecker.ParseType(tool.ReturnType, table);
        var symbol     = table.LookupDocument(SymbolKind.Tool, tool.Name);

        return new HirTool(
            id,
            tool.Name,
            parameters,
            returnType,
            tool.Sandbox,
            tool.Capabilities,
            tool.Timeout,
            symbol,
            tool.Location);
    }

    private static HirTest BuildTest(
        TestDeclaration test,
        Dictionary<string, SourceLocation> sourceMap)
    {
        var id = HirId.ForTest(test.Name);
        sourceMap[id.Value] = test.Location;
        return new HirTest(id, test.Name, test.Location);
    }

    private static HirParameter BuildParameter(ParameterDeclaration p, SymbolTable table) =>
        new(p.Name, AISharpTypeChecker.ParseType(p.Type, table), p.Ownership,
            new SourceLocation(1, 1, 0));

    private static HirModelCall BuildModelCall(ModelCallDeclaration mc, SymbolTable table)
    {
        AISharpType? outputType = mc.OutputSchemaReference is null
            ? null
            : (AISharpType)AISharpTypeChecker.ParseType(mc.OutputSchemaReference.Name, table);

        return new HirModelCall(
            mc.Provider,
            mc.Model,
            mc.Task,
            mc.Input,
            mc.OutputSchema,
            outputType,
            mc.Replay,
            mc.CapturesProvenance,
            mc.OutputSchemaReference,
            mc.Provenance,
            mc.Location);
    }

    // ── statement lowering ───────────────────────────────────────────────────

    private static HirStatement LowerStatement(FunctionStatement stmt, SymbolTable table) =>
        stmt switch
        {
            LetStatement let =>
                new HirLetStatement(
                    let.Name,
                    let.TypeAnnotation is null
                        ? null
                        : AISharpTypeChecker.ParseType(let.TypeAnnotation, table),
                    let.Initializer.Text,
                    AISharpTypeChecker.InferExpressionType(let.Initializer.Text),
                    let.Location),

            ReturnStatement ret =>
                new HirReturnStatement(
                    ret.Expression.Text,
                    AISharpTypeChecker.InferExpressionType(ret.Expression.Text),
                    ret.Location),

            ExpressionStatement expr =>
                new HirExpressionStatement(expr.Expression.Text, expr.Location),

            TaskGroupStatement tg =>
                new HirTaskGroupStatement(
                    tg.Name,
                    tg.Statements.Select(s => LowerStatement(s, table)).ToList(),
                    tg.Location),

            UsingStatement usingStatement =>
                new HirUsingStatement(
                    usingStatement.ResourceName,
                    usingStatement.Statements.Select(s => LowerStatement(s, table)).ToList(),
                    usingStatement.Location),

            SpawnStatement spawn =>
                new HirSpawnStatement(spawn.Name, spawn.Expression.Text, spawn.Location),

            AllStatement all =>
                new HirAllStatement(all.Targets, all.Location),

            MatchStatement match =>
                new HirMatchStatement(
                    match.Scrutinee,
                    match.Arms.Select(a => new HirMatchArm(a.Pattern, a.Body, a.Location)).ToList(),
                    match.Location),

            _ => new HirExpressionStatement(stmt.ToString() ?? string.Empty, stmt.Location)
        };
}
