namespace AISharp.Runtime;

// ─── Tool capability grant ────────────────────────────────────────────────────

/// <summary>Policy governing whether explicit tool capability grants are required.</summary>
public enum ToolCapabilityPolicy
{
    /// <summary>Tool capabilities must be explicitly declared and granted. Default.</summary>
    DeniedUnlessGranted,
    /// <summary>Tool capabilities are allowed without explicit grants (not recommended).</summary>
    AllowAll
}

// ─── Unsafe interop policy ────────────────────────────────────────────────────

/// <summary>Policy governing whether unsafe interop operations are permitted.</summary>
public enum UnsafeInteropPolicy
{
    /// <summary>Unsafe interop is forbidden. Default.</summary>
    Forbidden,
    /// <summary>Unsafe interop is explicitly allowed (requires declaration).</summary>
    Allowed
}

// ─── Provenance requirement ───────────────────────────────────────────────────

/// <summary>Policy governing whether provenance records are required for model calls.</summary>
public enum ProvenanceRequirement
{
    /// <summary>Provenance is required for every model call. Default for remote calls.</summary>
    Required,
    /// <summary>Provenance is recommended but not enforced by the engine.</summary>
    Optional
}

// ─── Extended policy decision ─────────────────────────────────────────────────

/// <summary>
/// A fully-resolved policy decision including all policy engine dimensions.
/// Extends <see cref="PolicyDecision"/> with tool capability, unsafe interop,
/// and provenance requirements.
/// </summary>
public sealed record PolicyEngineDecision(
    string PolicyName,
    ClassificationLevel Classification,
    InferencePolicy Inference,
    TrainingPolicy Training,
    TelemetryPolicy Telemetry,
    ExternalTextPolicy ExternalText,
    CapabilitiesPolicy Capabilities,
    ToolCapabilityPolicy ToolCapability,
    UnsafeInteropPolicy UnsafeInterop,
    ProvenanceRequirement ProvenanceRequirement)
{
    /// <summary>Creates a safe local-only default decision for the given policy name.</summary>
    public static PolicyEngineDecision SafeDefault(string policyName) =>
        new(policyName,
            ClassificationLevel.Internal,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated,
            ToolCapabilityPolicy.DeniedUnlessGranted,
            UnsafeInteropPolicy.Forbidden,
            ProvenanceRequirement.Required);
}

// ─── Policy diagnostic ────────────────────────────────────────────────────────

/// <summary>Severity of a policy diagnostic.</summary>
public enum PolicyDiagnosticSeverity
{
    /// <summary>Policy warning — allowed but not recommended.</summary>
    Warning,
    /// <summary>Policy error — the operation is denied.</summary>
    Error
}

/// <summary>
/// A structured diagnostic record emitted by the <see cref="PolicyEngine"/> for every denial.
/// </summary>
public sealed record PolicyDiagnostic(
    string PolicyName,
    string Code,
    string Message,
    PolicyDiagnosticSeverity Severity);

// ─── Policy evaluation request ────────────────────────────────────────────────

/// <summary>
/// Describes an operation that the <see cref="PolicyEngine"/> should evaluate.
/// </summary>
public sealed record PolicyEvaluationRequest(
    string PolicyName,
    ModelProviderKind ProviderKind,
    bool HasExplicitToolGrant = false,
    bool RequestsUnsafeInterop = false,
    bool HasProvenance = false,
    string[]? RequestedCapabilities = null);

// ─── Policy evaluation result ─────────────────────────────────────────────────

/// <summary>
/// The result of a <see cref="PolicyEngine.Evaluate"/> call.
/// Captures all diagnostics (denials and warnings) produced during evaluation.
/// </summary>
public sealed class PolicyEvaluationResult
{
    private readonly List<PolicyDiagnostic> _diagnostics;

    internal PolicyEvaluationResult(bool allowed, List<PolicyDiagnostic> diagnostics)
    {
        Allowed = allowed;
        _diagnostics = diagnostics;
    }

    /// <summary>Whether the evaluated operation is permitted by policy.</summary>
    public bool Allowed { get; }

    /// <summary>All diagnostics emitted during evaluation (errors block; warnings don't).</summary>
    public IReadOnlyList<PolicyDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Only the error-severity diagnostics.</summary>
    public IEnumerable<PolicyDiagnostic> Errors =>
        _diagnostics.Where(d => d.Severity == PolicyDiagnosticSeverity.Error);

    /// <summary>Only the warning-severity diagnostics.</summary>
    public IEnumerable<PolicyDiagnostic> Warnings =>
        _diagnostics.Where(d => d.Severity == PolicyDiagnosticSeverity.Warning);

    /// <summary>
    /// Throws a <see cref="PolicyViolationException"/> for the first error diagnostic,
    /// if any exist.
    /// </summary>
    public void ThrowIfDenied()
    {
        var first = Errors.FirstOrDefault();
        if (first is not null)
            throw new PolicyViolationException(first.PolicyName, first.Message);
    }
}

// ─── Policy engine ────────────────────────────────────────────────────────────

/// <summary>
/// Centralised AI# policy decision engine.
/// Combines source policy declarations, project configuration, and runtime defaults
/// to evaluate every AI# operation against the active policy.
/// </summary>
/// <remarks>
/// Evaluation is non-throwing: results are returned as <see cref="PolicyEvaluationResult"/>
/// with structured <see cref="PolicyDiagnostic"/> records for every denial.
/// Call <see cref="PolicyEvaluationResult.ThrowIfDenied"/> to convert to an exception at a boundary.
/// </remarks>
public sealed class PolicyEngine
{
    private readonly Dictionary<string, PolicyEngineDecision> _decisions =
        new(StringComparer.Ordinal);

    /// <summary>Constructs an engine with the default local-first safe policy.</summary>
    public PolicyEngine() { }

    /// <summary>Constructs an engine with an explicit set of policy decisions.</summary>
    public PolicyEngine(IEnumerable<PolicyEngineDecision> decisions)
    {
        foreach (var d in decisions)
            _decisions[d.PolicyName] = d;
    }

    /// <summary>Constructs an engine from a <see cref="PolicyRuntimeContext"/> and extended decisions.</summary>
    public PolicyEngine(PolicyRuntimeContext context, IEnumerable<PolicyEngineDecision>? extended = null)
    {
        if (extended is not null)
            foreach (var d in extended)
                _decisions[d.PolicyName] = d;
    }

    /// <summary>
    /// Returns the fully-resolved policy decision for the given policy name,
    /// or a safe local-only default if the name is not registered.
    /// </summary>
    public PolicyEngineDecision GetPolicy(string policyName) =>
        _decisions.TryGetValue(policyName, out var d)
            ? d
            : PolicyEngineDecision.SafeDefault(policyName);

    /// <summary>
    /// Evaluates the given operation request against the active policy.
    /// Every denial produces a structured <see cref="PolicyDiagnostic"/> in the result.
    /// </summary>
    public PolicyEvaluationResult Evaluate(PolicyEvaluationRequest request)
    {
        var decision = GetPolicy(request.PolicyName);
        var diagnostics = new List<PolicyDiagnostic>();
        bool allowed = true;

        // ── Remote inference gate ─────────────────────────────────────────────
        if (request.ProviderKind == ModelProviderKind.Remote &&
            decision.Inference != InferencePolicy.RemoteOptIn)
        {
            allowed = false;
            diagnostics.Add(new PolicyDiagnostic(
                request.PolicyName,
                "POLICY_REMOTE_OPT_IN_REQUIRED",
                "Remote inference requires 'inference = remote_opt_in' in the source policy declaration.",
                PolicyDiagnosticSeverity.Error));
        }

        // ── Telemetry gate ────────────────────────────────────────────────────
        if (decision.Telemetry != TelemetryPolicy.Forbidden)
        {
            diagnostics.Add(new PolicyDiagnostic(
                request.PolicyName,
                "POLICY_TELEMETRY_ENABLED",
                "Telemetry is enabled; ensure consent and data minimisation requirements are met.",
                PolicyDiagnosticSeverity.Warning));
        }

        // ── Training gate ─────────────────────────────────────────────────────
        if (decision.Training != TrainingPolicy.Forbidden)
        {
            diagnostics.Add(new PolicyDiagnostic(
                request.PolicyName,
                "POLICY_TRAINING_ENABLED",
                "Training data use is enabled; verify explicit consent.",
                PolicyDiagnosticSeverity.Warning));
        }

        // ── External text gate ────────────────────────────────────────────────
        // ExternalTextPolicy currently only has Tainted, which is always the safe value.
        // No denial possible with the current enum; included for forward compatibility.

        // ── Tool capability gate ──────────────────────────────────────────────
        if (decision.ToolCapability == ToolCapabilityPolicy.DeniedUnlessGranted &&
            (request.RequestedCapabilities?.Length > 0) && !request.HasExplicitToolGrant)
        {
            allowed = false;
            diagnostics.Add(new PolicyDiagnostic(
                request.PolicyName,
                "POLICY_TOOL_CAPABILITY_NOT_GRANTED",
                "Requested tool capabilities must be explicitly declared and granted in the policy.",
                PolicyDiagnosticSeverity.Error));
        }

        // ── Unsafe interop gate ───────────────────────────────────────────────
        if (request.RequestsUnsafeInterop &&
            decision.UnsafeInterop == UnsafeInteropPolicy.Forbidden)
        {
            allowed = false;
            diagnostics.Add(new PolicyDiagnostic(
                request.PolicyName,
                "POLICY_UNSAFE_INTEROP_FORBIDDEN",
                "Unsafe interop is forbidden by policy. Declare 'unsafe_interop = allowed' to opt in.",
                PolicyDiagnosticSeverity.Error));
        }

        // ── Provenance gate ───────────────────────────────────────────────────
        if (decision.ProvenanceRequirement == ProvenanceRequirement.Required &&
            !request.HasProvenance)
        {
            // For remote provider kind, provenance is required. For local, warn.
            if (request.ProviderKind == ModelProviderKind.Remote)
            {
                allowed = false;
                diagnostics.Add(new PolicyDiagnostic(
                    request.PolicyName,
                    "POLICY_PROVENANCE_REQUIRED",
                    "Provenance record is required for all remote model calls by active policy.",
                    PolicyDiagnosticSeverity.Error));
            }
            else
            {
                diagnostics.Add(new PolicyDiagnostic(
                    request.PolicyName,
                    "POLICY_PROVENANCE_RECOMMENDED",
                    "Provenance record is recommended for local model calls.",
                    PolicyDiagnosticSeverity.Warning));
            }
        }

        return new PolicyEvaluationResult(allowed, diagnostics);
    }
}
