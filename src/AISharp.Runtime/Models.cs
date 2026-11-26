namespace AISharp.Runtime;

// ─── Model provider kind ──────────────────────────────────────────────────────

/// <summary>Distinguishes local from remote model providers.</summary>
public enum ModelProviderKind
{
    /// <summary>Model runs locally (default; safe for sensitive data).</summary>
    Local,
    /// <summary>Model is hosted remotely; requires explicit opt-in policy.</summary>
    Remote
}

// ─── Model request ────────────────────────────────────────────────────────────

/// <summary>
/// A fully-specified model call request from AI# lowered code.
/// The schema name is required so output validation can be applied before
/// the response reaches user code.
/// </summary>
public sealed record ModelRequest(
    string Provider,
    string Model,
    string Task,
    string InputPayload,
    string? SchemaName,
    ModelProviderKind ProviderKind,
    string? PolicyName,
    ModelReplayOptions? Replay = null,
    CancellationToken Cancellation = default,
    TimeSpan? Timeout = null);

// ─── Replay options ───────────────────────────────────────────────────────────

/// <summary>Deterministic replay metadata for model calls.</summary>
public sealed record ModelReplayOptions(
    long? Seed,
    double? Temperature);

// ─── Model response ───────────────────────────────────────────────────────────

/// <summary>Response returned by an <see cref="IAISharpModelRuntime"/> implementation.</summary>
public sealed record ModelResponse(
    bool Success,
    string? OutputPayload,
    string? ErrorMessage,
    string? OutputHash,
    ModelCallProvenance? Provenance);

// ─── Model runtime interface ──────────────────────────────────────────────────

/// <summary>
/// Provider-neutral model runtime interface used by AI# lowered code.
/// Implementations must enforce the declared policy before dispatching calls.
/// </summary>
public interface IAISharpModelRuntime
{
    /// <summary>The provider kind this runtime implements.</summary>
    ModelProviderKind ProviderKind { get; }

    /// <summary>Returns true when the runtime is reachable and ready.</summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a model call and returns a structured response.
    /// Implementations must reject requests if the policy does not permit the provider kind.
    /// </summary>
    Task<ModelResponse> ExecuteAsync(ModelRequest request);
}

// ─── Fake local model runtime ─────────────────────────────────────────────────

/// <summary>
/// Deterministic fake <see cref="IAISharpModelRuntime"/> for testing.
/// Callers register handlers by (provider, model, task) key; unregistered calls fail clearly.
/// Remote provider requests are rejected unless the injected policy context permits them.
/// </summary>
public sealed class FakeLocalModelRuntime : IAISharpModelRuntime
{
    private readonly PolicyRuntimeContext _policy;
    private readonly Dictionary<string, Func<ModelRequest, ModelResponse>> _handlers = new();

    /// <param name="policy">Policy context used to validate remote provider requests.</param>
    public FakeLocalModelRuntime(PolicyRuntimeContext? policy = null)
    {
        _policy = policy ?? new PolicyRuntimeContext();
    }

    /// <inheritdoc/>
    public ModelProviderKind ProviderKind => ModelProviderKind.Local;

    /// <inheritdoc/>
    public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>Registers a fake handler for the given provider/model/task combination.</summary>
    public void Register(string provider, string model, string task,
        Func<ModelRequest, ModelResponse> handler)
    {
        _handlers[$"{provider}/{model}/{task}"] = handler;
    }

    /// <inheritdoc/>
    public Task<ModelResponse> ExecuteAsync(ModelRequest request)
    {
        if (request.SchemaName is null)
            throw new ArgumentException(
                $"AI# model call to '{request.Model}' is missing required output schema.", nameof(request));

        if (request.ProviderKind == ModelProviderKind.Remote)
        {
            if (request.PolicyName is null)
                throw new PolicyViolationException("(unspecified)",
                    "Remote model call requires a non-null policy name.");
            _policy.RequireRemoteInferenceAllowed(request.PolicyName);
        }

        var key = $"{request.Provider}/{request.Model}/{request.Task}";
        if (_handlers.TryGetValue(key, out var handler))
            return Task.FromResult(handler(request));

        return Task.FromResult(new ModelResponse(
            false,
            null,
            $"FakeLocalModelRuntime: no handler registered for '{key}'.",
            null,
            null));
    }
}
