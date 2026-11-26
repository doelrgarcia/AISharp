namespace AISharp.Compiler;

using System.Text.RegularExpressions;

// ── Effect model ──────────────────────────────────────────────────────────────

/// <summary>Effect analysis result for a single function or actor handler.</summary>
public sealed record FunctionEffectInfo(
    string FunctionName,
    IReadOnlyList<string> DeclaredEffects,
    IReadOnlyList<string> InferredRequiredEffects,
    bool IsCompliant);

/// <summary>Aggregated result of effect checking over an entire document.</summary>
public sealed record EffectCheckResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<FunctionEffectInfo> FunctionEffects);

// ── Effect checker ────────────────────────────────────────────────────────────

/// <summary>
/// Verifies that every effect used by a function is listed in its
/// <c>effects [...]</c> declaration and that actor handlers also comply.
///
/// Diagnostic codes:
///   AI3030 – function uses an effect that is not declared in its signature.
///   AI3031 – actor handler uses an effect that is not declared in its signature.
/// </summary>
public static class AISharpEffectChecker
{
    /// <summary>Built-in effects recognised by the language.</summary>
    public static readonly IReadOnlySet<string> BuiltinEffects =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "io", "time", "random", "model", "network", "unsafe_ffi"
        };

    // Expression sub-string patterns that imply a required effect.
    private static readonly (string Pattern, string Effect)[] s_expressionPatterns =
    [
        // network
        (".get(",    "network"),
        (".post(",   "network"),
        (".put(",    "network"),
        (".delete(", "network"),
        (".patch(",  "network"),
        ("http.",    "network"),
        ("https.",   "network"),
        ("fetch(",   "network"),
        ("request(", "network"),
        ("socket(",  "network"),

        // io
        ("File.",     "io"),
        ("fs.",       "io"),
        ("readFile",  "io"),
        ("writeFile", "io"),
        ("process.",  "io"),
        ("stdin",     "io"),
        ("stdout",    "io"),
        ("stderr",    "io"),

        // time
        ("time.",     "time"),
        ("clock.",    "time"),
        ("Date.now",  "time"),
        ("DateTime.", "time"),
        ("sleep(",    "time"),

        // random
        ("random(",   "random"),
        ("Random.",   "random"),
        ("uuid(",     "random"),
        (".random(",  "random"),

        // unsafe_ffi
        ("ffi.",      "unsafe_ffi"),
        ("native.",   "unsafe_ffi"),
        ("unsafe(",   "unsafe_ffi"),
        ("[extern",   "unsafe_ffi"),
    ];

    /// <summary>
    /// Checks effect compliance for all functions and actor handlers in
    /// <paramref name="document"/> and returns diagnostics plus per-function metadata.
    /// </summary>
    public static EffectCheckResult Check(
        AISharpDocument document,
        SymbolTable symbolTable)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(symbolTable);

        var diagnostics   = new List<Diagnostic>();
        var functionInfos = new List<FunctionEffectInfo>();
        var functionsByName = BuildFunctionMap(document);
        var inferredCache = new Dictionary<FunctionDeclaration, IReadOnlyList<string>>();

        foreach (var fn in document.Functions)
        {
            var info = AnalyseFunction(fn, "AI3030", diagnostics, functionsByName, inferredCache);
            functionInfos.Add(info);
        }

        foreach (var actor in document.Actors)
        {
            foreach (var handler in actor.Handlers)
            {
                var info = AnalyseFunction(handler, "AI3031", diagnostics, functionsByName, inferredCache);
                // Qualify the name so the JSON makes the actor context clear.
                functionInfos.Add(new FunctionEffectInfo(
                    $"{actor.Name}.{info.FunctionName}",
                    info.DeclaredEffects,
                    info.InferredRequiredEffects,
                    info.IsCompliant));
            }
        }

        return new EffectCheckResult(diagnostics, functionInfos);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private static FunctionEffectInfo AnalyseFunction(
        FunctionDeclaration fn,
        string diagnosticCode,
        List<Diagnostic> diagnostics,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionDeclaration>> functionsByName,
        Dictionary<FunctionDeclaration, IReadOnlyList<string>> inferredCache)
    {
        var declared = new HashSet<string>(fn.Effects, StringComparer.Ordinal);
        var inferred = InferRequiredEffects(fn, functionsByName, inferredCache, []);
        var compliant = true;

        foreach (var effect in inferred)
        {
            if (!declared.Contains(effect))
            {
                compliant = false;
                diagnostics.Add(new Diagnostic(
                    diagnosticCode,
                    DiagnosticSeverity.Error,
                    $"Function '{fn.Name}' uses or propagates effect '{effect}' but does not declare it in 'effects [...]'. Declaring this effect prevents silent effect escalation.",
                    fn.Location));
            }
        }

        return new FunctionEffectInfo(fn.Name, [.. declared], [.. inferred], compliant);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<FunctionDeclaration>> BuildFunctionMap(AISharpDocument document)
    {
        var functions = new Dictionary<string, List<FunctionDeclaration>>(StringComparer.Ordinal);

        foreach (var fn in document.Functions)
            AddFunction(fn);

        foreach (var actor in document.Actors)
            foreach (var handler in actor.Handlers)
                AddFunction(handler);

        return functions.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<FunctionDeclaration>)kvp.Value,
            StringComparer.Ordinal);

        void AddFunction(FunctionDeclaration fn)
        {
            if (!functions.TryGetValue(fn.Name, out var overloads))
            {
                overloads = [];
                functions[fn.Name] = overloads;
            }

            overloads.Add(fn);
        }
    }

    private static IReadOnlyList<string> InferRequiredEffects(
        FunctionDeclaration fn,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionDeclaration>> functionsByName,
        Dictionary<FunctionDeclaration, IReadOnlyList<string>> inferredCache,
        HashSet<FunctionDeclaration> activeStack)
    {
        if (inferredCache.TryGetValue(fn, out var cached))
            return cached;

        if (!activeStack.Add(fn))
            return fn.Effects;

        var effects = new SortedSet<string>(StringComparer.Ordinal);

        // Explicit model calls always require the 'model' effect.
        if (fn.ModelCalls.Count > 0)
            effects.Add("model");

        // Scan every statement in the function body.
        foreach (var stmt in fn.Body.Statements)
            CollectFromStatement(stmt, effects, functionsByName, inferredCache, activeStack);

        activeStack.Remove(fn);

        var inferred = (IReadOnlyList<string>)[.. effects];
        inferredCache[fn] = inferred;
        return inferred;
    }

    private static void CollectFromStatement(
        FunctionStatement stmt,
        SortedSet<string> effects,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionDeclaration>> functionsByName,
        Dictionary<FunctionDeclaration, IReadOnlyList<string>> inferredCache,
        HashSet<FunctionDeclaration> activeStack)
    {
        switch (stmt)
        {
            case LetStatement let:
                CollectFromText(let.Initializer.Text, effects, functionsByName, inferredCache, activeStack);
                break;

            case ReturnStatement ret:
                CollectFromText(ret.Expression.Text, effects, functionsByName, inferredCache, activeStack);
                break;

            case ExpressionStatement expr:
                CollectFromText(expr.Expression.Text, effects, functionsByName, inferredCache, activeStack);
                break;

            case TaskGroupStatement tg:
                foreach (var inner in tg.Statements)
                    CollectFromStatement(inner, effects, functionsByName, inferredCache, activeStack);
                break;

            case SpawnStatement sp:
                CollectFromText(sp.Expression.Text, effects, functionsByName, inferredCache, activeStack);
                break;

            case MatchStatement match:
                CollectFromText(match.Scrutinee, effects, functionsByName, inferredCache, activeStack);
                foreach (var arm in match.Arms)
                    CollectFromText(arm.Body, effects, functionsByName, inferredCache, activeStack);
                break;
        }
    }

    private static void CollectFromText(
        string text,
        SortedSet<string> effects,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionDeclaration>> functionsByName,
        Dictionary<FunctionDeclaration, IReadOnlyList<string>> inferredCache,
        HashSet<FunctionDeclaration> activeStack)
    {
        foreach (var (pattern, effect) in s_expressionPatterns)
            if (text.Contains(pattern, StringComparison.Ordinal))
                effects.Add(effect);

        foreach (var (functionName, declarations) in functionsByName)
        {
            if (!ContainsSimpleFunctionCall(text, functionName))
                continue;

            foreach (var declaration in declarations)
            {
                foreach (var effect in InferRequiredEffects(declaration, functionsByName, inferredCache, activeStack))
                    effects.Add(effect);
            }
        }
    }

    private static bool ContainsSimpleFunctionCall(string text, string functionName)
        => Regex.IsMatch(
            text,
            $@"(?<![A-Za-z0-9_#\.]){Regex.Escape(functionName)}\s*\(",
            RegexOptions.CultureInvariant);
}
