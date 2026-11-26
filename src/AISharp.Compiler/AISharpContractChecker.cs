namespace AISharp.Compiler;

// ── Contract check model ──────────────────────────────────────────────────────

/// <summary>
/// Semantic analysis result for a single contract clause on a function.
/// Carries both diagnostic metadata and lowering/verification metadata.
/// </summary>
public sealed record ContractCheckInfo(
    string FunctionName,
    ContractKind Kind,
    string Expression,
    SourceLocation Location,
    /// <summary>True when the expression was determined to be Boolean; null when undetermined.</summary>
    bool? IsBoolean,
    IReadOnlyList<string> AvailableNames,
    IReadOnlyList<string> UnknownNames,
    string VerificationHook,
    ContractCheckPhase CheckPhase,
    bool EmitRuntimeCheck,
    bool EmitVerificationCondition,
    RuntimeContractCheckMetadata RuntimeCheck,
    VerificationConditionMetadata VerificationCondition);

/// <summary>Metadata consumed by future lowering to emit runtime contract checks.</summary>
public sealed record RuntimeContractCheckMetadata(
    bool Emit,
    ContractCheckPhase Phase,
    string FunctionName,
    ContractKind Kind,
    string Expression,
    SourceLocation Location);

/// <summary>Metadata consumed by future verifier integration to build verification conditions.</summary>
public sealed record VerificationConditionMetadata(
    bool Emit,
    string Hook,
    string FunctionName,
    ContractKind Kind,
    string Expression,
    SourceLocation Location,
    IReadOnlyList<string> AvailableNames);

/// <summary>Aggregated contract-check result over an entire document.</summary>
public sealed record ContractCheckResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ContractCheckInfo> ContractInfos);

// ── Contract checker ──────────────────────────────────────────────────────────

/// <summary>
/// Checks that contract expressions (<c>requires</c>, <c>ensures</c>, <c>invariant</c>)
/// are Boolean, that names referenced inside them are in scope, and produces lowering
/// metadata for runtime-check generation and future verifier integration.
///
/// Diagnostic codes:
///   AI3040 – contract expression could not be determined to be Boolean.
///   AI3041 – contract expression references an unknown identifier.
/// </summary>
public static class AISharpContractChecker
{
    // Single-character names are assumed to be lambda/local pattern variables.
    private static readonly HashSet<string> s_builtinNames = new(StringComparer.Ordinal)
    {
        "true", "false", "empty", "null", "unit",
        "old", "this", "self", "count", "length", "size",
        "min", "max", "abs", "all", "any", "none", "some",
        "filter", "map", "fold", "reduce", "first", "last",
        "Ok", "Err", "Some", "None", "is"
    };

    // Tokens that indicate a Boolean expression context.
    private static readonly string[] s_booleanIndicators =
    [
        "!=", "==", ">=", "<=", ">", "<", " is ", " && ", " || ", "!",
        "=>", "true", "false"
    ];

    /// <summary>
    /// Checks all contracts on functions and actor handlers in <paramref name="document"/>.
    /// </summary>
    public static ContractCheckResult Check(
        AISharpDocument document,
        SymbolTable symbolTable)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(symbolTable);

        var diagnostics  = new List<Diagnostic>();
        var contractInfos = new List<ContractCheckInfo>();

        foreach (var fn in document.Functions)
            CheckFunctionContracts(fn, symbolTable, diagnostics, contractInfos, []);

        foreach (var actor in document.Actors)
        {
            var actorStateNames = actor.StateMembers.Select(state => state.Name).ToArray();
            foreach (var handler in actor.Handlers)
                CheckFunctionContracts(handler, symbolTable, diagnostics, contractInfos, actorStateNames);
        }

        return new ContractCheckResult(diagnostics, contractInfos);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private static void CheckFunctionContracts(
        FunctionDeclaration fn,
        SymbolTable symbolTable,
        List<Diagnostic> diagnostics,
        List<ContractCheckInfo> infos,
        IReadOnlyList<string> actorStateNames)
    {
        if (fn.Contracts.Count == 0)
            return;

        var parameterTypes = fn.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.Type,
            StringComparer.Ordinal);

        // Union-case names declared in the document are valid in contracts.
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sym in symbolTable.AllDocumentSymbols)
            typeNames.Add(sym.Name);

        foreach (var contract in fn.Contracts)
        {
            var availableNameTypes = BuildAvailableNameTypes(contract.Kind, parameterTypes, fn.ReturnType, actorStateNames);
            bool? isBoolean = DetermineIsBoolean(contract.Expression, availableNameTypes);
            var unknownNames = FindUnknownNames(
                contract.Expression,
                availableNameTypes.Keys.ToHashSet(StringComparer.Ordinal),
                typeNames,
                resultInScope: availableNameTypes.ContainsKey("result"));

            // AI3040: expression must be Boolean.
            if (isBoolean == false)
            {
                diagnostics.Add(new Diagnostic(
                    "AI3040",
                    DiagnosticSeverity.Error,
                    $"Contract '{contract.VerificationHook}' on '{fn.Name}' has a non-Boolean expression: '{contract.Expression}'.",
                    contract.Location));
            }

            // AI3041: unknown name references.
            foreach (var unknown in unknownNames)
            {
                diagnostics.Add(new Diagnostic(
                    "AI3041",
                    DiagnosticSeverity.Error,
                    $"Contract '{contract.VerificationHook}' on '{fn.Name}' references unknown name '{unknown}'.",
                    contract.Location));
            }

            var runtimeCheck = new RuntimeContractCheckMetadata(
                contract.EmitRuntimeCheck,
                contract.CheckPhase,
                fn.Name,
                contract.Kind,
                contract.Expression,
                contract.Location);

            var verificationCondition = new VerificationConditionMetadata(
                contract.EmitVerificationCondition,
                contract.VerificationHook,
                fn.Name,
                contract.Kind,
                contract.Expression,
                contract.Location,
                availableNameTypes.Keys.ToArray());

            infos.Add(new ContractCheckInfo(
                fn.Name,
                contract.Kind,
                contract.Expression,
                contract.Location,
                isBoolean,
                availableNameTypes.Keys.ToArray(),
                unknownNames,
                contract.VerificationHook,
                contract.CheckPhase,
                contract.EmitRuntimeCheck,
                contract.EmitVerificationCondition,
                runtimeCheck,
                verificationCondition));
        }
    }

    private static Dictionary<string, string> BuildAvailableNameTypes(
        ContractKind kind,
        IReadOnlyDictionary<string, string> parameterTypes,
        string returnType,
        IReadOnlyList<string> actorStateNames)
    {
        var available = new Dictionary<string, string>(parameterTypes, StringComparer.Ordinal);

        if (kind is ContractKind.Ensures or ContractKind.Invariant)
            available["result"] = returnType;

        if (kind is ContractKind.Invariant)
        {
            foreach (var stateName in actorStateNames)
                available.TryAdd(stateName, "unknown");
        }

        return available;
    }

    /// <summary>
    /// Determines whether an expression is Boolean or non-Boolean.
    /// </summary>
    private static bool? DetermineIsBoolean(
        string expression,
        IReadOnlyDictionary<string, string> availableNameTypes)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();

        // Trivially Boolean literals.
        if (trimmed is "true" or "false")
            return true;

        // Presence of any Boolean-indicating token means Boolean.
        foreach (var indicator in s_booleanIndicators)
            if (trimmed.Contains(indicator, StringComparison.Ordinal))
                return true;

        if (availableNameTypes.TryGetValue(trimmed, out var typeName)
            && IsBooleanTypeName(typeName))
        {
            return true;
        }

        return false;
    }

    private static bool IsBooleanTypeName(string typeName) =>
        string.Equals(typeName.Trim(), "bool", StringComparison.Ordinal);

    /// <summary>
    /// Extracts identifier tokens from <paramref name="expression"/> that cannot be
    /// resolved to a known name in scope.
    /// </summary>
    private static IReadOnlyList<string> FindUnknownNames(
        string expression,
        HashSet<string> paramNames,
        HashSet<string> typeNames,
        bool resultInScope)
    {
        var unknowns = new List<string>();
        var identifierPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?<id>[A-Za-z_][A-Za-z0-9_]*)\b");

        foreach (System.Text.RegularExpressions.Match m in identifierPattern.Matches(expression))
        {
            var id = m.Groups["id"].Value;
            var index = m.Index;

            if (index > 0 && expression[index - 1] == '.')
                continue;

            // Single-character names are lambda / pattern variables.
            if (id.Length == 1)
                continue;

            // Built-in names.
            if (s_builtinNames.Contains(id))
                continue;

            // `result` in ensures / invariant scope.
            if (resultInScope && id == "result")
                continue;

            // Function parameters.
            if (paramNames.Contains(id))
                continue;

            // Type or symbol names (types, schemas, capabilities, union cases).
            if (typeNames.Contains(id))
                continue;

            // Names beginning with uppercase are treated as type/case references.
            if (char.IsUpper(id[0]))
                continue;

            unknowns.Add(id);
        }

        // Deduplicate while preserving order.
        return unknowns.Distinct(StringComparer.Ordinal).ToList();
    }
}
