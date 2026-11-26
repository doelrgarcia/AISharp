namespace AISharp.Runtime;

// ─── Policy values ────────────────────────────────────────────────────────────

/// <summary>Inference locality policy.</summary>
public enum InferencePolicy
{
    /// <summary>All inference must use local models only.</summary>
    LocalOnly,
    /// <summary>Remote inference is explicitly opted into.</summary>
    RemoteOptIn
}

/// <summary>Training data policy.</summary>
public enum TrainingPolicy
{
    /// <summary>Data must not be used for model training.</summary>
    Forbidden,
    /// <summary>Training use has been explicitly opted into.</summary>
    OptIn
}

/// <summary>Telemetry collection policy.</summary>
public enum TelemetryPolicy
{
    /// <summary>Telemetry collection is forbidden.</summary>
    Forbidden,
    /// <summary>Only aggregate (non-identifiable) telemetry is permitted.</summary>
    OptInAggregate
}

/// <summary>External-text handling policy.</summary>
public enum ExternalTextPolicy
{
    /// <summary>External text is always tainted and treated as untrusted.</summary>
    Tainted
}

/// <summary>Capability-access separation policy.</summary>
public enum CapabilitiesPolicy
{
    /// <summary>AI capabilities are separated from tool invocation privileges.</summary>
    Separated
}

/// <summary>Data classification level.</summary>
public enum ClassificationLevel
{
    /// <summary>Data may be shared publicly.</summary>
    Public,
    /// <summary>Data is internal and should not be shared outside the organisation.</summary>
    Internal,
    /// <summary>Data is secret and must not be transmitted or logged without explicit consent.</summary>
    Secret
}

// ─── Policy decision ──────────────────────────────────────────────────────────

/// <summary>
/// A resolved policy decision for a single source-declared policy.
/// The runtime combines source policy declarations, project configuration,
/// and runtime defaults to produce this record.
/// </summary>
public sealed record PolicyDecision(
    string PolicyName,
    ClassificationLevel Classification,
    InferencePolicy Inference,
    TrainingPolicy Training,
    TelemetryPolicy Telemetry,
    ExternalTextPolicy ExternalText,
    CapabilitiesPolicy Capabilities);

// ─── Policy violation ─────────────────────────────────────────────────────────

/// <summary>Thrown when an AI# operation violates the active policy.</summary>
public sealed class PolicyViolationException : Exception
{
    /// <summary>Initialises a new <see cref="PolicyViolationException"/>.</summary>
    public PolicyViolationException(string policyName, string reason)
        : base($"AI# policy violation in '{policyName}': {reason}")
    {
        PolicyName = policyName;
        Reason = reason;
    }

    /// <summary>The name of the policy that was violated.</summary>
    public string PolicyName { get; }

    /// <summary>Human-readable description of the violation.</summary>
    public string Reason { get; }
}

// ─── Policy runtime context ───────────────────────────────────────────────────

/// <summary>
/// Runtime enforcement of AI# source policy declarations.
/// Default configuration is local-first and deny-by-default for remote operations.
/// </summary>
public sealed class PolicyRuntimeContext
{
    private readonly Dictionary<string, PolicyDecision> _decisions = new(StringComparer.Ordinal);

    /// <summary>Constructs a context with the default local-first safe policy.</summary>
    public PolicyRuntimeContext() { }

    /// <summary>Constructs a context with an explicit set of policy decisions.</summary>
    public PolicyRuntimeContext(IEnumerable<PolicyDecision> decisions)
    {
        foreach (var d in decisions)
            _decisions[d.PolicyName] = d;
    }

    /// <summary>
    /// Returns the decision for the given policy, or a safe local-only default if not found.
    /// </summary>
    public PolicyDecision GetPolicy(string policyName) =>
        _decisions.TryGetValue(policyName, out var decision)
            ? decision
            : new PolicyDecision(
                policyName,
                ClassificationLevel.Internal,
                InferencePolicy.LocalOnly,
                TrainingPolicy.Forbidden,
                TelemetryPolicy.Forbidden,
                ExternalTextPolicy.Tainted,
                CapabilitiesPolicy.Separated);

    /// <summary>
    /// Enforces that the given policy permits remote inference.
    /// Throws <see cref="PolicyViolationException"/> if it does not.
    /// </summary>
    public void RequireRemoteInferenceAllowed(string policyName)
    {
        var policy = GetPolicy(policyName);
        if (policy.Inference != InferencePolicy.RemoteOptIn)
            throw new PolicyViolationException(
                policyName,
                "Remote inference requires 'inference = remote_opt_in' in the source policy declaration.");
    }

    /// <summary>
    /// Enforces that the given policy declares external text as tainted.
    /// Throws <see cref="PolicyViolationException"/> if it does not.
    /// </summary>
    public void RequireExternalTextTainted(string policyName)
    {
        var policy = GetPolicy(policyName);
        if (policy.ExternalText != ExternalTextPolicy.Tainted)
            throw new PolicyViolationException(
                policyName,
                "External text must be declared 'external_text = tainted'.");
    }

    /// <summary>
    /// Enforces that the given policy declares capability separation.
    /// Throws <see cref="PolicyViolationException"/> if it does not.
    /// </summary>
    public void RequireCapabilitiesSeparated(string policyName)
    {
        var policy = GetPolicy(policyName);
        if (policy.Capabilities != CapabilitiesPolicy.Separated)
            throw new PolicyViolationException(
                policyName,
                "AI capabilities must be declared 'capabilities = separated'.");
    }
}
