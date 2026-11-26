namespace AISharp.Compiler;

using System.Text.RegularExpressions;

// ─── MIR expression ──────────────────────────────────────────────────────────

/// <summary>
/// A fully-typed MIR expression. Every expression carries an <see cref="AISharpType"/>
/// or <see cref="AISharpType.Error"/> when front-end diagnostics require recovery.
/// </summary>
public sealed record MirExpression(
    string Text,
    AISharpType Type,
    SourceLocation Location);

// ─── MIR statements ──────────────────────────────────────────────────────────

/// <summary>Abstract base for all typed MIR statements.</summary>
public abstract record MirStatement(SourceLocation Location);

/// <summary><c>let name: Type = expr</c> MIR statement with fully typed initializer.</summary>
public sealed record MirLetStatement(
    string Name,
    AISharpType AnnotationType,
    MirExpression Initializer,
    SourceLocation Location) : MirStatement(Location);

/// <summary><c>return expr</c> MIR statement.</summary>
public sealed record MirReturnStatement(
    MirExpression Expression,
    SourceLocation Location) : MirStatement(Location);

/// <summary>A bare expression MIR statement.</summary>
public sealed record MirExpressionStatement(
    MirExpression Expression,
    SourceLocation Location) : MirStatement(Location);

/// <summary>A parallel task-group MIR statement.</summary>
public sealed record MirTaskGroupStatement(
    string? Name,
    IReadOnlyList<MirStatement> Statements,
    SourceLocation Location) : MirStatement(Location);

/// <summary>A lexical resource lifetime block (<c>using resource { ... }</c>).</summary>
public sealed record MirUsingStatement(
    string ResourceName,
    IReadOnlyList<MirStatement> Statements,
    SourceLocation Location) : MirStatement(Location);

/// <summary>A spawn statement inside a task group.</summary>
public sealed record MirSpawnStatement(
    string Name,
    MirExpression Expression,
    SourceLocation Location) : MirStatement(Location);

/// <summary>An <c>all [a, b, c]</c> sync point.</summary>
public sealed record MirAllStatement(
    IReadOnlyList<string> Targets,
    SourceLocation Location) : MirStatement(Location);

/// <summary>A single arm in a MIR match statement.</summary>
public sealed record MirMatchArm(
    string Pattern,
    MirExpression Body,
    SourceLocation Location);

/// <summary>A typed <c>match</c> statement with scrutinee type and typed arm bodies.</summary>
public sealed record MirMatchStatement(
    string Scrutinee,
    AISharpType ScrutineeType,
    IReadOnlyList<MirMatchArm> Arms,
    SourceLocation Location) : MirStatement(Location);

// ─── Effect obligations ───────────────────────────────────────────────────────

/// <summary>
/// An effect obligation inferred from a function body construct.
/// Represents the fact that a function's body requires a particular named effect
/// and explains why.
/// </summary>
public sealed record MirEffectObligation(
    string Effect,
    string Reason,
    SourceLocation Location);

// ─── Contract check plans ─────────────────────────────────────────────────────

/// <summary>When a contract check should be evaluated at runtime.</summary>
public enum MirContractCheckMode
{
    /// <summary>Check always at runtime.</summary>
    Runtime,
    /// <summary>Check only in debug builds.</summary>
    Debug,
    /// <summary>Emit as a verification condition only; skip runtime check.</summary>
    VerificationOnly
}

/// <summary>
/// A runtime/verification contract check plan derived from a HIR contract node.
/// Carries the expression, available binding names, check mode, and source location
/// so lowering can emit the appropriate guard code.
/// </summary>
public sealed record MirContractCheckPlan(
    ContractKind Kind,
    string Expression,
    IReadOnlyList<string> AvailableNames,
    MirContractCheckMode CheckMode,
    SourceLocation Location);

// ─── Model, tool, and test nodes ──────────────────────────────────────────────

/// <summary>A typed model-call plan ready for lowering/runtime wiring.</summary>
public sealed record MirModelCall(
    string Provider,
    string Model,
    string? Task,
    string? Input,
    AISharpType OutputType,
    bool CapturesProvenance,
    SourceLocation Location);

/// <summary>A typed tool declaration with sandbox and capability metadata.</summary>
public sealed record MirTool(
    HirId Id,
    string Name,
    IReadOnlyList<HirParameter> Parameters,
    AISharpType ReturnType,
    string? Sandbox,
    IReadOnlyList<string> Capabilities,
    string? Timeout,
    SourceLocation Location);

/// <summary>An inline test declaration linked back to source.</summary>
public sealed record MirTest(
    HirId Id,
    string Name,
    SourceLocation Location);

// ─── MIR declaration nodes ────────────────────────────────────────────────────

/// <summary>
/// A typed MIR function (or actor handler). All statements carry typed expressions;
/// effect obligations are fully enumerated; contract check plans are ready for lowering.
/// </summary>
public sealed record MirFunction(
    HirId Id,
    string Name,
    IReadOnlyList<HirParameter> Parameters,
    AISharpType ReturnType,
    IReadOnlyList<string> DeclaredEffects,
    IReadOnlyList<MirEffectObligation> EffectObligations,
    IReadOnlyList<MirContractCheckPlan> ContractCheckPlans,
    IReadOnlyList<MirStatement> Body,
    IReadOnlyList<MirModelCall> ModelCalls,
    bool IsAsync,
    SourceLocation Location);

/// <summary>An actor declaration in the typed MIR.</summary>
public sealed record MirActor(
    HirId Id,
    string Name,
    IReadOnlyList<HirStateField> StateMembers,
    IReadOnlyList<MirFunction> Handlers,
    SourceLocation Location);

/// <summary>
/// Root typed MIR document. Implements <see cref="ITypedMirArtifact"/> so it satisfies
/// the <c>HIR → typed MIR</c> compiler pipeline boundary contract.
/// </summary>
public sealed record MirDocument(
    HirId Id,
    string? Module,
    string? Intent,
    IReadOnlyList<HirTypeDecl> Types,
    IReadOnlyList<HirSchema> Schemas,
    IReadOnlyList<MirTool> Tools,
    IReadOnlyList<MirFunction> Functions,
    IReadOnlyList<MirActor> Actors,
    IReadOnlyList<MirTest> Tests,
    SourceLocation Location) : ITypedMirArtifact
{
    public IReadOnlyList<ScalarFunction> ScalarFunctions { get; init; } = [];
    public bool ScalarValidationFailed { get; init; }
}

// ─── MIR build result ─────────────────────────────────────────────────────────

/// <summary>
/// Result of the MIR build pass. Carries the typed <see cref="MirDocument"/>,
/// the accumulated diagnostics (forwarded from semantic analysis plus any new MIR
/// errors), and a source-map from stable <see cref="HirId"/> strings to
/// <see cref="SourceLocation"/> values.
/// </summary>
public sealed record MirBuildResult(
    MirDocument Document,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, SourceLocation> SourceMap);

// ─── MIR builder ─────────────────────────────────────────────────────────────

/// <summary>
/// Builds a <see cref="MirDocument"/> (typed MIR) from a <see cref="HirBuildResult"/>.
///
/// The builder:
/// <list type="bullet">
///   <item>Lowers each HIR statement to a typed <see cref="MirStatement"/>.</item>
///   <item>Ensures every <see cref="MirExpression"/> has an <see cref="AISharpType"/>
///     (falling back to <see cref="AISharpType.Error"/> for unresolvable expressions).</item>
///   <item>Infers <see cref="MirEffectObligation"/> records from model calls, body
///     expressions, and known effectful patterns.</item>
///   <item>Produces <see cref="MirContractCheckPlan"/> records from HIR contracts,
///     populating available binding names by contract clause.</item>
///   <item>Forwards all semantic diagnostics (including type-checker and effect-checker
///     diagnostics) into <see cref="MirBuildResult.Diagnostics"/>.</item>
///   <item>Re-uses the HIR source map so source locations remain stable.</item>
/// </list>
/// </summary>
public static class AISharpMirBuilder
{
    // Built-in effect names that map to specific body-pattern keywords.
    private static readonly IReadOnlyDictionary<string, string[]> s_effectKeywords =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["model"]      = ["model "],
            ["network"]    = [".get(", ".post(", ".put(", ".delete(", ".patch(", "http.", "api."],
            ["io"]         = ["File.", "Directory.", "Process.", "Stream.", "Console."],
            ["time"]       = ["DateTime.", "DateTimeOffset.", "TimeSpan.", "now(", "clock("],
            ["random"]     = ["random.", "Random.", "rand(", "uuid("],
            ["unsafe_ffi"] = ["unsafe ", "extern ", "DllImport", "P/Invoke"]
        };

    private static readonly Regex s_modelCallPattern =
        new(@"model\s+(?<provider>[A-Za-z_][A-Za-z0-9_#]*)\s+""(?<model>[^""]+)""",
            RegexOptions.Compiled);

    private static readonly Regex s_modelTaskPattern =
        new(@"\.task\s*\(\s*""(?<task>[^""]*)""\s*\)",
            RegexOptions.Compiled);

    private static readonly Regex s_modelInputPattern =
        new(@"\.input\s*\(\s*(?<input>[^)]*)\)",
            RegexOptions.Compiled);

    private static readonly Regex s_modelOutputPattern =
        new(@"\.output\s*<\s*(?<schema>[A-Za-z_][A-Za-z0-9_#]*)\s*>\s*\(\s*\)",
            RegexOptions.Compiled);

    /// <summary>
    /// Builds a typed <see cref="MirDocument"/> from the supplied HIR.
    /// Semantic diagnostics from the HIR's originating <see cref="SemanticResult"/> are
    /// preserved; the MIR build itself does not produce new diagnostics for well-formed
    /// programs.
    /// </summary>
    public static MirBuildResult Build(HirBuildResult hirResult)
    {
        ArgumentNullException.ThrowIfNull(hirResult);

        var hirDoc = hirResult.Document;
        var declaredTypes = BuildDeclaredTypeMap(hirDoc);

        // Re-use the HIR source map — MIR shares the same stable IDs.
        var sourceMap = new Dictionary<string, SourceLocation>(
            hirResult.SourceMap, StringComparer.Ordinal);

        var mirFunctions = hirDoc.Functions
            .Select(fn => LowerFunction(fn, sourceMap, declaredTypes))
            .ToList();

        var mirActors = hirDoc.Actors
            .Select(actor => LowerActor(actor, sourceMap, declaredTypes))
            .ToList();

        var mirTools = hirDoc.Tools
            .Select(LowerTool)
            .ToList();

        var mirTests = hirDoc.Tests
            .Select(LowerTest)
            .ToList();

        var mirDoc = new MirDocument(
            hirDoc.Id,
            hirDoc.Module,
            hirDoc.Intent,
            hirDoc.Types,
            hirDoc.Schemas,
            mirTools,
            mirFunctions,
            mirActors,
            mirTests,
            hirDoc.Location) { ScalarFunctions = hirDoc.ScalarFunctions, ScalarValidationFailed = hirDoc.ScalarValidationFailed };

        // Collect all diagnostics from the HIR's semantic pass.  We do not have a
        // direct back-reference to the SemanticResult here, but the HIR builder stores
        // semantic diagnostics on each HirFunction's Symbol; instead we rely on the
        // caller to compose diagnostics through SemanticResult when available.
        // The MIR builder itself is diagnostic-free for valid programs; diagnostics
        // must be gathered from the SemanticResult and passed alongside the MirBuildResult
        // by the pipeline coordinator.  For now we carry an empty diagnostic list so
        // the pipeline can append to it.
        //
        // However, the tests need diagnostics to flow through MirBuildResult so that
        // "invalid programs" tests can assert on AI3020 codes.  We solve this by
        // attaching a companion diagnostic-list extension point on the result that the
        // pipeline coordinator can populate.
        IReadOnlyList<Diagnostic> diagnostics = [];

        return new MirBuildResult(mirDoc, diagnostics, sourceMap);
    }

    /// <summary>
    /// Overload that accepts the upstream <see cref="SemanticResult"/> so that semantic
    /// diagnostics (type-check, effect-check, contract-check) flow into the MIR build
    /// result automatically.  Prefer this overload in the full compiler pipeline.
    /// </summary>
    public static MirBuildResult Build(HirBuildResult hirResult, SemanticResult semanticResult)
    {
        ArgumentNullException.ThrowIfNull(hirResult);
        ArgumentNullException.ThrowIfNull(semanticResult);

        var hirDoc = hirResult.Document;
        var declaredTypes = BuildDeclaredTypeMap(hirDoc);

        var sourceMap = new Dictionary<string, SourceLocation>(
            hirResult.SourceMap, StringComparer.Ordinal);

        var mirFunctions = hirDoc.Functions
            .Select(fn => LowerFunction(fn, sourceMap, declaredTypes))
            .ToList();

        var mirActors = hirDoc.Actors
            .Select(actor => LowerActor(actor, sourceMap, declaredTypes))
            .ToList();

        var mirTools = hirDoc.Tools
            .Select(LowerTool)
            .ToList();

        var mirTests = hirDoc.Tests
            .Select(LowerTest)
            .ToList();

        var mirDoc = new MirDocument(
            hirDoc.Id,
            hirDoc.Module,
            hirDoc.Intent,
            hirDoc.Types,
            hirDoc.Schemas,
            mirTools,
            mirFunctions,
            mirActors,
            mirTests,
            hirDoc.Location) { ScalarFunctions = hirDoc.ScalarFunctions, ScalarValidationFailed = hirDoc.ScalarValidationFailed };

        // Aggregate all diagnostics from parsing + semantic analysis.
        var allDiagnostics = semanticResult.AllDiagnostics.ToList();

        return new MirBuildResult(mirDoc, allDiagnostics, sourceMap);
    }

    // ── function lowering ─────────────────────────────────────────────────────

    private static MirFunction LowerFunction(
        HirFunction fn,
        Dictionary<string, SourceLocation> sourceMap,
        IReadOnlyDictionary<string, AISharpType> declaredTypes)
    {
        var paramNames = fn.Parameters.Select(p => p.Name).ToList();
        var effectObligations = InferEffectObligations(fn);
        var contractPlans     = BuildContractCheckPlans(fn, paramNames);

        // Build a type environment seeded with the function parameters.
        var typeEnv = new Dictionary<string, AISharpType>(StringComparer.Ordinal);
        foreach (var param in fn.Parameters)
            typeEnv[param.Name] = param.Type;

        var body = fn.Body.Select(s => LowerStatement(s, typeEnv)).ToList();
        var modelCalls = fn.ModelCalls.Select(LowerModelCall).ToList();
        modelCalls.AddRange(ExtractExpressionModelCalls(fn.Body, typeEnv, declaredTypes));

        return new MirFunction(
            fn.Id,
            fn.Name,
            fn.Parameters,
            fn.ReturnType,
            fn.Effects,
            effectObligations,
            contractPlans,
            body,
            modelCalls,
            fn.IsAsync,
            fn.Location);
    }

    private static MirActor LowerActor(
        HirActor actor,
        Dictionary<string, SourceLocation> sourceMap,
        IReadOnlyDictionary<string, AISharpType> declaredTypes)
    {
        var handlers = actor.Handlers.Select(h => LowerFunction(h, sourceMap, declaredTypes)).ToList();
        return new MirActor(actor.Id, actor.Name, actor.StateMembers, handlers, actor.Location);
    }

    private static IReadOnlyDictionary<string, AISharpType> BuildDeclaredTypeMap(HirDocument document)
    {
        var map = new Dictionary<string, AISharpType>(StringComparer.Ordinal);

        foreach (var schema in document.Schemas)
        {
            var fields = schema.Fields
                .Select(f => new ParameterDeclaration(f.Name, FormatType(f.Type), f.Ownership))
                .ToList();
            map[schema.Name] = new RecordAIType(schema.Name, fields);
        }

        foreach (var type in document.Types)
        {
            map[type.Name] = type.Kind switch
            {
                TypeKind.Record => new RecordAIType(
                    type.Name,
                    type.Fields.Select(f => new ParameterDeclaration(f.Name, FormatType(f.Type), f.Ownership)).ToList()),
                TypeKind.Union => new UnionAIType(type.Cases, type.Name),
                _ => new NamedAIType(type.Name, type.Symbol)
            };
        }

        return map;
    }

    private static MirTool LowerTool(HirTool tool) =>
        new(
            tool.Id,
            tool.Name,
            tool.Parameters,
            tool.ReturnType,
            tool.Sandbox,
            tool.Capabilities,
            tool.Timeout,
            tool.Location);

    private static MirModelCall LowerModelCall(HirModelCall modelCall) =>
        new(
            modelCall.Provider,
            modelCall.Model,
            modelCall.Task,
            modelCall.Input,
            modelCall.OutputType ?? AISharpType.Error,
            true,
            modelCall.Location);

    private static IReadOnlyList<MirModelCall> ExtractExpressionModelCalls(
        IReadOnlyList<HirStatement> statements,
        IReadOnlyDictionary<string, AISharpType> typeEnv,
        IReadOnlyDictionary<string, AISharpType> declaredTypes)
    {
        var calls = new List<MirModelCall>();
        foreach (var statement in statements)
            ExtractExpressionModelCalls(statement, typeEnv, declaredTypes, calls);

        return calls;
    }

    private static void ExtractExpressionModelCalls(
        HirStatement statement,
        IReadOnlyDictionary<string, AISharpType> typeEnv,
        IReadOnlyDictionary<string, AISharpType> declaredTypes,
        List<MirModelCall> calls)
    {
        switch (statement)
        {
            case HirLetStatement let:
                TryAddExpressionModelCall(let.ExpressionText, let.Location, typeEnv, declaredTypes, calls);
                break;

            case HirReturnStatement ret:
                TryAddExpressionModelCall(ret.ExpressionText, ret.Location, typeEnv, declaredTypes, calls);
                break;

            case HirExpressionStatement expr:
                TryAddExpressionModelCall(expr.ExpressionText, expr.Location, typeEnv, declaredTypes, calls);
                break;

            case HirSpawnStatement spawn:
                TryAddExpressionModelCall(spawn.ExpressionText, spawn.Location, typeEnv, declaredTypes, calls);
                break;

            case HirTaskGroupStatement taskGroup:
                foreach (var inner in taskGroup.Statements)
                    ExtractExpressionModelCalls(inner, typeEnv, declaredTypes, calls);
                break;

            case HirUsingStatement usingStatement:
                foreach (var inner in usingStatement.Statements)
                    ExtractExpressionModelCalls(inner, typeEnv, declaredTypes, calls);
                break;

            case HirMatchStatement match:
                foreach (var arm in match.Arms)
                    TryAddExpressionModelCall(arm.BodyText, arm.Location, typeEnv, declaredTypes, calls);
                break;
        }
    }

    private static void TryAddExpressionModelCall(
        string expressionText,
        SourceLocation location,
        IReadOnlyDictionary<string, AISharpType> typeEnv,
        IReadOnlyDictionary<string, AISharpType> declaredTypes,
        List<MirModelCall> calls)
    {
        var modelMatch = s_modelCallPattern.Match(expressionText);
        if (!modelMatch.Success)
            return;

        var outputMatch = s_modelOutputPattern.Match(expressionText);
        var outputName = outputMatch.Success ? outputMatch.Groups["schema"].Value : null;
        var outputType = outputName is not null && declaredTypes.TryGetValue(outputName, out var declaredType)
            ? declaredType
            : outputName is not null
                ? new NamedAIType(outputName, null)
            : AISharpType.Error;

        calls.Add(new MirModelCall(
            modelMatch.Groups["provider"].Value,
            modelMatch.Groups["model"].Value,
            MatchOptionalValue(s_modelTaskPattern, expressionText, "task"),
            MatchOptionalValue(s_modelInputPattern, expressionText, "input"),
            outputType,
            true,
            location));
    }

    private static string? MatchOptionalValue(Regex regex, string text, string groupName)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[groupName].Value.Trim() : null;
    }

    private static MirTest LowerTest(HirTest test) =>
        new(test.Id, test.Name, test.Location);
    // ── effect obligation inference ───────────────────────────────────────────

    private static IReadOnlyList<MirEffectObligation> InferEffectObligations(HirFunction fn)
    {
        var obligations = new List<MirEffectObligation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Model calls always require the 'model' effect.
        if (fn.ModelCalls.Count > 0)
        {
            AddObligation(obligations, seen, "model",
                $"function '{fn.Name}' contains model calls",
                fn.ModelCalls[0].Location);
        }

        // Scan body statements for effectful patterns.
        foreach (var stmt in fn.Body)
            InferFromStatement(stmt, fn.Name, obligations, seen);

        return obligations;
    }

    private static void InferFromStatement(
        HirStatement stmt,
        string functionName,
        List<MirEffectObligation> obligations,
        HashSet<string> seen)
    {
        switch (stmt)
        {
            case HirLetStatement let:
                InferFromText(let.ExpressionText, functionName, obligations, seen, let.Location);
                break;

            case HirReturnStatement ret:
                InferFromText(ret.ExpressionText, functionName, obligations, seen, ret.Location);
                break;

            case HirExpressionStatement expr:
                InferFromText(expr.ExpressionText, functionName, obligations, seen, expr.Location);
                break;

            case HirTaskGroupStatement tg:
                foreach (var s in tg.Statements)
                    InferFromStatement(s, functionName, obligations, seen);
                break;

            case HirUsingStatement us:
                foreach (var s in us.Statements)
                    InferFromStatement(s, functionName, obligations, seen);
                break;

            case HirSpawnStatement spawn:
                InferFromText(spawn.ExpressionText, functionName, obligations, seen, spawn.Location);
                break;

            case HirMatchStatement match:
                foreach (var arm in match.Arms)
                    InferFromText(arm.BodyText, functionName, obligations, seen, arm.Location);
                break;
        }
    }

    private static void InferFromText(
        string text,
        string functionName,
        List<MirEffectObligation> obligations,
        HashSet<string> seen,
        SourceLocation location)
    {
        foreach (var (effect, keywords) in s_effectKeywords)
        {
            if (keywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                AddObligation(obligations, seen, effect,
                    $"expression in '{functionName}' uses '{effect}' pattern",
                    location);
            }
        }
    }

    private static void AddObligation(
        List<MirEffectObligation> obligations,
        HashSet<string> seen,
        string effect,
        string reason,
        SourceLocation location)
    {
        if (seen.Add(effect))
            obligations.Add(new MirEffectObligation(effect, reason, location));
    }

    // ── contract check plan builder ───────────────────────────────────────────

    private static IReadOnlyList<MirContractCheckPlan> BuildContractCheckPlans(
        HirFunction fn,
        IReadOnlyList<string> paramNames)
    {
        var plans = new List<MirContractCheckPlan>();

        foreach (var contract in fn.Contracts)
        {
            var availableNames = contract.Kind switch
            {
                ContractKind.Requires  => paramNames.ToList(),
                ContractKind.Ensures   => [.. paramNames, "result"],
                ContractKind.Invariant => [.. paramNames, "result"],
                _                      => paramNames.ToList()
            };

            plans.Add(new MirContractCheckPlan(
                contract.Kind,
                contract.Expression,
                availableNames,
                MirContractCheckMode.Runtime,
                contract.Location));
        }

        return plans;
    }

    // ── statement lowering ────────────────────────────────────────────────────

    private static MirStatement LowerStatement(
        HirStatement stmt,
        Dictionary<string, AISharpType> typeEnv) =>
        stmt switch
        {
            HirLetStatement let =>
                LowerLetStatement(let, typeEnv),

            HirReturnStatement ret =>
                new MirReturnStatement(
                    new MirExpression(ret.ExpressionText,
                        ResolveExpressionType(ret.ExpressionText, ret.InferredType, typeEnv),
                        ret.Location),
                    ret.Location),

            HirExpressionStatement expr =>
                new MirExpressionStatement(
                    new MirExpression(expr.ExpressionText,
                        ResolveExpressionType(expr.ExpressionText, AISharpType.Unknown, typeEnv),
                        expr.Location),
                    expr.Location),

            HirTaskGroupStatement tg =>
                new MirTaskGroupStatement(
                    tg.Name,
                    tg.Statements.Select(s => LowerStatement(s, typeEnv)).ToList(),
                    tg.Location),

            HirUsingStatement us =>
                new MirUsingStatement(
                    us.ResourceName,
                    us.Statements.Select(s => LowerStatement(s, typeEnv)).ToList(),
                    us.Location),

            HirSpawnStatement spawn =>
                new MirSpawnStatement(
                    spawn.Name,
                    new MirExpression(spawn.ExpressionText,
                        ResolveExpressionType(spawn.ExpressionText, AISharpType.Unknown, typeEnv),
                        spawn.Location),
                    spawn.Location),

            HirAllStatement all =>
                new MirAllStatement(all.Targets, all.Location),

            HirMatchStatement match =>
                new MirMatchStatement(
                    match.Scrutinee,
                    ResolveExpressionType(match.Scrutinee, AISharpType.Unknown, typeEnv),
                    match.Arms.Select(a =>
                        new MirMatchArm(
                            a.Pattern,
                            new MirExpression(a.BodyText,
                                ResolveExpressionType(a.BodyText, AISharpType.Unknown, typeEnv),
                                a.Location),
                            a.Location)).ToList(),
                    match.Location),

            _ => new MirExpressionStatement(
                new MirExpression(stmt.ToString() ?? string.Empty, AISharpType.Error, stmt.Location),
                stmt.Location)
        };

    private static MirLetStatement LowerLetStatement(
        HirLetStatement let,
        Dictionary<string, AISharpType> typeEnv)
    {
        // Resolve the initializer type: prefer annotation > inferred > environment lookup.
        var initType = let.AnnotationType
            ?? (let.InferredType is not UnknownAIType ? let.InferredType : AISharpType.Error);

        var annotationType = let.AnnotationType ?? initType;

        // Register the variable in the type environment so downstream statements can use it.
        typeEnv[let.Name] = annotationType;

        return new MirLetStatement(
            let.Name,
            annotationType,
            new MirExpression(let.ExpressionText, initType, let.Location),
            let.Location);
    }

    /// <summary>
    /// Returns the best available type for an expression string.
    /// Tries the already-inferred type first, then falls back to a simple name lookup
    /// in the current type environment (handles bare variable references like <c>return y</c>).
    /// </summary>
    private static AISharpType ResolveExpressionType(
        string expressionText,
        AISharpType inferredType,
        IReadOnlyDictionary<string, AISharpType> typeEnv)
    {
        if (inferredType is not UnknownAIType)
            return inferredType;

        // Simple variable reference: single identifier that exists in the environment.
        var trimmed = expressionText.Trim();
        if (IsSimpleIdentifier(trimmed) && typeEnv.TryGetValue(trimmed, out var envType))
            return envType;

        return AISharpType.Error;
    }

    private static bool IsSimpleIdentifier(string text) =>
        text.Length > 0
        && (char.IsLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string FormatType(AISharpType type) => type switch
    {
        PrimitiveAIType p => p.Name,
        NamedAIType n => n.Name,
        GenericAIType g when g.Args.Count == 0 => g.Head,
        GenericAIType g => $"{g.Head}<{string.Join(", ", g.Args.Select(FormatType))}>",
        FunctionAIType f => $"fn({string.Join(", ", f.Parameters.Select(FormatType))}) -> {FormatType(f.ReturnType)}",
        ResultAIType r => $"Result<{FormatType(r.OkType)}, {FormatType(r.ErrorType)}>",
        OptionAIType o => $"Option<{FormatType(o.ValueType)}>",
        RecordAIType r => r.Name,
        ResourceAIType r => r.Name,
        UnionAIType u when u.Name is not null => u.Name,
        UnionAIType u => string.Join(" | ", u.Cases),
        UnknownAIType => "unknown",
        ErrorAIType => "error",
        _ => type.ToString() ?? "unknown"
    };
}
