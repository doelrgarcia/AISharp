using System.Security.Cryptography;
using System.Text;

namespace AISharp.Runtime;

// ─── Tool request ─────────────────────────────────────────────────────────────

/// <summary>A request to invoke a sandboxed AI# tool.</summary>
public sealed record ToolRequest(
    string ToolName,
    string InputPayload,
    IReadOnlyList<string> RequestedCapabilities,
    TimeSpan Timeout,
    string? InputSchemaName = null,
    string? OutputSchemaName = null,
    CancellationToken Cancellation = default);

// ─── Tool response ────────────────────────────────────────────────────────────

/// <summary>Response from a sandboxed tool invocation.</summary>
public sealed record ToolResponse(
    bool Success,
    string? OutputPayload,
    string? ErrorMessage,
    bool TimedOut,
    TimeSpan Duration,
    ToolInvocationProvenance? Provenance);

// ─── Tool capability ──────────────────────────────────────────────────────────

/// <summary>Represents a declared tool capability.</summary>
public sealed record ToolCapability(string Name);

// ─── Tool sandbox interface ───────────────────────────────────────────────────

/// <summary>
/// Abstraction for executing AI# tools in an isolated sandbox.
/// The default AI# safety model requires <c>sandbox = wasm</c> for declared tools.
/// Sandbox implementations must:
/// <list type="bullet">
///   <item>Deny capabilities not present in <see cref="ToolRequest.RequestedCapabilities"/>.</item>
///   <item>Enforce the declared timeout and report <see cref="ToolResponse.TimedOut"/>.</item>
///   <item>Schema-validate output when <see cref="ToolRequest.OutputSchemaName"/> is set.</item>
///   <item>Capture provenance including input/output hashes and duration.</item>
/// </list>
/// </summary>
public interface IAISharpToolSandbox
{
    /// <summary>Returns the set of capabilities this sandbox can grant.</summary>
    IReadOnlySet<string> AvailableCapabilities { get; }

    /// <summary>
    /// Executes the named tool in the sandbox.
    /// Throws <see cref="ToolCapabilityDeniedException"/> for undeclared capabilities.
    /// </summary>
    Task<ToolResponse> InvokeAsync(ToolRequest request);
}

// ─── Capability denied exception ─────────────────────────────────────────────

/// <summary>Thrown when a tool requests a capability that was not declared or granted.</summary>
public sealed class ToolCapabilityDeniedException : Exception
{
    /// <summary>Initialises a new <see cref="ToolCapabilityDeniedException"/>.</summary>
    public ToolCapabilityDeniedException(string toolName, string capability)
        : base($"AI# tool '{toolName}' requested undeclared capability '{capability}'.")
    {
        ToolName = toolName;
        Capability = capability;
    }

    /// <summary>The name of the tool that requested the undeclared capability.</summary>
    public string ToolName { get; }

    /// <summary>The undeclared capability that was denied.</summary>
    public string Capability { get; }
}

// ─── Fake sandbox for testing ─────────────────────────────────────────────────

/// <summary>
/// In-memory fake sandbox implementation for tests.
/// Responses are pre-registered by tool name.
/// Enforces:
/// <list type="bullet">
///   <item>Declared-capability gate (throws <see cref="ToolCapabilityDeniedException"/>).</item>
///   <item>Timeout via combined <see cref="CancellationTokenSource"/> (returns <see cref="ToolResponse.TimedOut"/> = true).</item>
///   <item>Output schema validation when <see cref="ToolRequest.OutputSchemaName"/> is set and a <see cref="SchemaRegistry"/> is provided.</item>
///   <item>Provenance capture: SHA-256 hashes of input/output payloads, granted capabilities, and duration.</item>
/// </list>
/// </summary>
public sealed class FakeToolSandbox : IAISharpToolSandbox
{
    private readonly Dictionary<string, Func<ToolRequest, ToolResponse>> _handlers
        = new(StringComparer.Ordinal);

    private readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
    private readonly SchemaRegistry? _schemas;

    /// <param name="schemas">Optional schema registry for output validation.</param>
    public FakeToolSandbox(SchemaRegistry? schemas = null) => _schemas = schemas;

    /// <summary>Registers a capability that this fake sandbox can grant.</summary>
    public void AddCapability(string capability) => _capabilities.Add(capability);

    /// <summary>Registers a response factory for the given tool name.</summary>
    public void Register(string toolName, Func<ToolRequest, ToolResponse> handler) =>
        _handlers[toolName] = handler;

    /// <inheritdoc/>
    public IReadOnlySet<string> AvailableCapabilities => _capabilities;

    /// <inheritdoc/>
    public async Task<ToolResponse> InvokeAsync(ToolRequest request)
    {
        foreach (var cap in request.RequestedCapabilities)
        {
            if (!_capabilities.Contains(cap))
                throw new ToolCapabilityDeniedException(request.ToolName, cap);
        }

        // Build combined timeout + cancellation token.
        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(
            request.Cancellation, timeoutCts.Token);

        var start = DateTimeOffset.UtcNow;
        var inputHash = ComputeHash(request.InputPayload);

        ToolResponse response;
        try
        {
            if (!_handlers.TryGetValue(request.ToolName, out var handler))
            {
                var duration = DateTimeOffset.UtcNow - start;
                return BuildResponse(
                    false, null,
                    $"Tool '{request.ToolName}' not registered in fake sandbox.",
                    false, duration, request, inputHash, null);
            }

            // Run the handler on a background task so timeout can interrupt it.
            var handlerTask = Task.Run(() => handler(request), combined.Token);
            var completed = await Task.WhenAny(handlerTask, Task.Delay(request.Timeout));

            if (completed != handlerTask || timeoutCts.IsCancellationRequested)
            {
                var elapsed = DateTimeOffset.UtcNow - start;
                return BuildResponse(false, null,
                    $"Tool '{request.ToolName}' timed out after {request.Timeout.TotalMilliseconds:0}ms.",
                    true, elapsed, request, inputHash, null);
            }

            var elapsed2 = DateTimeOffset.UtcNow - start;
            var rawResponse = await handlerTask;
            response = rawResponse with { Duration = elapsed2 };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var elapsed = DateTimeOffset.UtcNow - start;
            return BuildResponse(false, null,
                $"Tool '{request.ToolName}' timed out after {request.Timeout.TotalMilliseconds:0}ms.",
                true, elapsed, request, inputHash, null);
        }

        // Output schema validation.
        if (response.Success
            && response.OutputPayload is not null
            && request.OutputSchemaName is not null
            && _schemas is not null)
        {
            var validationResult = _schemas.Validate(
                request.OutputSchemaName, response.OutputPayload);
            if (!validationResult.IsValid)
            {
                var errors = string.Join("; ", validationResult.Errors.Select(e => e.Message));
                return BuildResponse(
                    false, response.OutputPayload,
                    $"Tool '{request.ToolName}' output failed schema validation: {errors}",
                    false, response.Duration, request, inputHash, response.OutputPayload);
            }
        }

        var outputHash = response.OutputPayload is not null
            ? ComputeHash(response.OutputPayload) : null;

        return BuildResponse(
            response.Success, response.OutputPayload, response.ErrorMessage,
            false, response.Duration, request, inputHash, outputHash);
    }

    private static ToolResponse BuildResponse(
        bool success, string? output, string? error, bool timedOut,
        TimeSpan duration, ToolRequest request, string inputHash, string? outputHash)
    {
        var provenance = new ToolInvocationProvenance(
            DateTimeOffset.UtcNow,
            request.ToolName,
            request.RequestedCapabilities.ToList(),
            inputHash,
            outputHash,
            duration,
            timedOut);

        return new ToolResponse(success, output, error, timedOut, duration, provenance);
    }

    private static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// ─── Wasm tool sandbox boundary ───────────────────────────────────────────────

/// <summary>
/// Boundary type for the Wasmtime-backed tool sandbox.
/// This class defines the integration surface without adding the Wasmtime NuGet dependency.
/// A real implementation would instantiate a <c>Wasmtime.Engine</c> and <c>Wasmtime.Store</c>
/// per invocation, load the tool's <c>.wasm</c> module, enforce capability grants via WASI
/// preview2 permission flags, and capture output/duration for provenance.
/// </summary>
/// <remarks>
/// Use <see cref="FakeToolSandbox"/> in tests.  Replace with a concrete
/// <c>WasmtimeToolSandbox</c> in production once the Wasmtime package is added.
/// </remarks>
public abstract class WasmToolSandboxBoundary : IAISharpToolSandbox
{
    /// <inheritdoc/>
    public abstract IReadOnlySet<string> AvailableCapabilities { get; }

    /// <inheritdoc/>
    public abstract Task<ToolResponse> InvokeAsync(ToolRequest request);

    /// <summary>
    /// Validates that all <paramref name="requestedCapabilities"/> are in
    /// <see cref="AvailableCapabilities"/>, throwing
    /// <see cref="ToolCapabilityDeniedException"/> on the first violation.
    /// </summary>
    protected void EnforceCapabilities(string toolName, IEnumerable<string> requestedCapabilities)
    {
        foreach (var cap in requestedCapabilities)
        {
            if (!AvailableCapabilities.Contains(cap))
                throw new ToolCapabilityDeniedException(toolName, cap);
        }
    }
}
