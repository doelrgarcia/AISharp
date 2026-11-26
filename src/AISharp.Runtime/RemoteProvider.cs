namespace AISharp.Runtime;

// ─── Remote provider gate ─────────────────────────────────────────────────────

/// <summary>
/// The result of the policy-gate check performed before dispatching a remote model call.
/// </summary>
public sealed record RemoteProviderGateResult(
    bool Allowed,
    string? DenialReason,
    string? DenialPolicyName);

// ─── Remote model provider boundary ──────────────────────────────────────────

/// <summary>
/// Abstract boundary class for remote AI model providers.
/// Enforces all required security policy gates before forwarding any call to the concrete provider.
/// Subclasses implement <see cref="DispatchAsync"/> for the actual HTTP/gRPC/network call.
/// </summary>
/// <remarks>
/// The gates enforced before every dispatch are:
/// <list type="bullet">
///   <item>Remote inference must be explicitly opted in (<c>inference = remote_opt_in</c>).</item>
///   <item>External text must be declared tainted (<c>external_text = tainted</c>).</item>
///   <item>AI capabilities must be separated from tool privileges (<c>capabilities = separated</c>).</item>
///   <item>Output schema must be named so schema validation can run before returning the response.</item>
///   <item>The concrete dispatch must return a <see cref="ModelCallProvenance"/> on success to satisfy the audit trail requirement.</item>
/// </list>
/// </remarks>
public abstract class RemoteModelProviderBoundary : IAISharpModelRuntime
{
    private readonly PolicyRuntimeContext _policy;

    /// <param name="policy">Policy context; defaults to the local-first safe policy when omitted.</param>
    protected RemoteModelProviderBoundary(PolicyRuntimeContext? policy = null)
    {
        _policy = policy ?? new PolicyRuntimeContext();
    }

    /// <inheritdoc/>
    public ModelProviderKind ProviderKind => ModelProviderKind.Remote;

    /// <inheritdoc/>
    public abstract Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// Thrown when the request is not a remote provider request or when output schema is missing.
    /// </exception>
    /// <exception cref="PolicyViolationException">
    /// Thrown when any policy gate rejects the call.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a successful <see cref="DispatchAsync"/> returns without provenance.
    /// </exception>
    public async Task<ModelResponse> ExecuteAsync(ModelRequest request)
    {
        if (request.ProviderKind != ModelProviderKind.Remote)
            throw new ArgumentException(
                "RemoteModelProviderBoundary only handles Remote provider requests.",
                nameof(request));

        if (request.PolicyName is null)
            throw new PolicyViolationException(
                "(unspecified)",
                "Remote model call requires a non-null policy name.");

        // Enforce all three policy gates in order.
        _policy.RequireRemoteInferenceAllowed(request.PolicyName);
        _policy.RequireExternalTextTainted(request.PolicyName);
        _policy.RequireCapabilitiesSeparated(request.PolicyName);

        if (request.SchemaName is null)
            throw new ArgumentException(
                $"Remote model call to '{request.Model}' is missing required output schema.",
                nameof(request));

        var response = await DispatchAsync(request).ConfigureAwait(false);

        if (response.Success && response.Provenance is null)
            throw new InvalidOperationException(
                $"Remote model provider '{GetType().Name}' returned Success=true without a Provenance record. " +
                "Every successful remote call must capture provenance for the audit trail.");

        return response.Success
            ? response with { Provenance = response.Provenance! with { ProviderKind = ModelProviderKind.Remote } }
            : response;
    }

    /// <summary>
    /// Dispatches the model call to the remote provider after all policy gates have passed.
    /// Implementations must populate <see cref="ModelResponse.Provenance"/> when
    /// <see cref="ModelResponse.Success"/> is <c>true</c>.
    /// Do not store or log raw input payloads; use only hashes in provenance records.
    /// </summary>
    protected abstract Task<ModelResponse> DispatchAsync(ModelRequest request);
}

// ─── Fake remote model provider ───────────────────────────────────────────────

/// <summary>
/// Deterministic fake remote model provider for testing.
/// Exercises the full <see cref="RemoteModelProviderBoundary"/> gate stack without making
/// any network calls. Callers register handlers by (provider, model, task) key.
/// </summary>
public sealed class FakeRemoteModelProvider : RemoteModelProviderBoundary
{
    private readonly Dictionary<string, Func<ModelRequest, ModelResponse>> _handlers = new();

    /// <param name="policy">Policy context; defaults to the local-first safe policy when omitted.</param>
    public FakeRemoteModelProvider(PolicyRuntimeContext? policy = null) : base(policy) { }

    /// <inheritdoc/>
    public override Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>Registers a fake handler for the given provider/model/task combination.</summary>
    public void Register(string provider, string model, string task,
        Func<ModelRequest, ModelResponse> handler)
        => _handlers[$"{provider}/{model}/{task}"] = handler;

    /// <inheritdoc/>
    protected override Task<ModelResponse> DispatchAsync(ModelRequest request)
    {
        var key = $"{request.Provider}/{request.Model}/{request.Task}";
        if (_handlers.TryGetValue(key, out var handler))
            return Task.FromResult(handler(request));

        return Task.FromResult(new ModelResponse(
            false,
            null,
            $"FakeRemoteModelProvider: no handler registered for '{key}'.",
            null,
            null));
    }
}
