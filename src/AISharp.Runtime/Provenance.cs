namespace AISharp.Runtime;

// ─── Provenance record kinds ──────────────────────────────────────────────────

/// <summary>Distinguishes kinds of AI# provenance records.</summary>
public enum ProvenanceRecordKind
{
    ModelCall,
    ToolInvocation,
    SourceHash,
    LoweredArtifact,
    BuildCommand,
    TestCommand,
    HumanApproval,
    PatchProposal
}

// ─── Base provenance record ───────────────────────────────────────────────────

/// <summary>
/// Base type for all AI# provenance records.
/// Records capture evidence for replay and audit without storing raw sensitive content.
/// </summary>
public abstract record ProvenanceRecord(
    ProvenanceRecordKind Kind,
    DateTimeOffset Timestamp,
    string? SessionId = null);

// ─── Model call provenance ────────────────────────────────────────────────────

/// <summary>Provenance record for a model call operation.</summary>
public sealed record ModelCallProvenance(
    DateTimeOffset Timestamp,
    string Provider,
    string Model,
    string Task,
    string? InputHash,
    string? OutputHash,
    string? SchemaName,
    bool IsReplay,
    long? ReplaySeed,
    double? ReplayTemperature,
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.ModelCall, Timestamp, SessionId)
{
    /// <summary>
    /// Kind of provider used for this model call. Defaults to local for backward-compatible
    /// construction; remote boundaries stamp successful remote calls explicitly.
    /// </summary>
    public ModelProviderKind ProviderKind { get; init; } = ModelProviderKind.Local;
}

// ─── Tool invocation provenance ───────────────────────────────────────────────

/// <summary>Provenance record for a sandboxed tool invocation.</summary>
public sealed record ToolInvocationProvenance(
    DateTimeOffset Timestamp,
    string ToolName,
    IReadOnlyList<string> GrantedCapabilities,
    string? InputHash,
    string? OutputHash,
    TimeSpan Duration,
    bool TimedOut,
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.ToolInvocation, Timestamp, SessionId);

// ─── Source hash provenance ───────────────────────────────────────────────────

/// <summary>Provenance record for a source file hash used during compilation.</summary>
public sealed record SourceHashProvenance(
    DateTimeOffset Timestamp,
    string SourcePath,
    string Hash,
    string HashAlgorithm = "sha256",
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.SourceHash, Timestamp, SessionId);

// ─── Lowered artifact provenance ─────────────────────────────────────────────

/// <summary>Provenance record for a lowered/generated C# artifact.</summary>
public sealed record LoweredArtifactProvenance(
    DateTimeOffset Timestamp,
    string ArtifactPath,
    string Hash,
    string SourcePath,
    string HashAlgorithm = "sha256",
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.LoweredArtifact, Timestamp, SessionId);

// ─── Build command provenance ─────────────────────────────────────────────────

/// <summary>Provenance record for a build command execution.</summary>
public sealed record BuildCommandProvenance(
    DateTimeOffset Timestamp,
    string Command,
    int ExitCode,
    string? ArtifactHash,
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.BuildCommand, Timestamp, SessionId);

// ─── Test command provenance ──────────────────────────────────────────────────

/// <summary>Provenance record for a test command execution.</summary>
public sealed record TestCommandProvenance(
    DateTimeOffset Timestamp,
    string Command,
    int PassedCount,
    int FailedCount,
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.TestCommand, Timestamp, SessionId);

// ─── Human approval provenance ────────────────────────────────────────────────

/// <summary>Provenance record for a human review or approval event.</summary>
public sealed record HumanApprovalProvenance(
    DateTimeOffset Timestamp,
    string ApprovalKind,
    string? ApprovedItemHash,
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.HumanApproval, Timestamp, SessionId);

// ─── Patch proposal provenance ────────────────────────────────────────────────

/// <summary>Provenance record for an AI-generated patch proposal.</summary>
public sealed record PatchProposalProvenance(
    DateTimeOffset Timestamp,
    string ProposalHash,
    int ChangedFileCount,
    string RiskLevel,
    bool WasApplied,
    string? SessionId = null)
    : ProvenanceRecord(ProvenanceRecordKind.PatchProposal, Timestamp, SessionId);

// ─── Provenance store interface ───────────────────────────────────────────────

/// <summary>
/// Abstraction for persisting and querying AI# provenance records.
/// Implementations should write stable JSON and must not store secrets or raw
/// sensitive prompt content.
/// </summary>
public interface IProvenanceStore
{
    /// <summary>Persists a provenance record.</summary>
    Task AppendAsync(ProvenanceRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns all provenance records, ordered by timestamp ascending.</summary>
    Task<IReadOnlyList<ProvenanceRecord>> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns records of the given kind.</summary>
    Task<IReadOnlyList<ProvenanceRecord>> ReadByKindAsync(
        ProvenanceRecordKind kind,
        CancellationToken cancellationToken = default);
}

// ─── In-memory provenance store ───────────────────────────────────────────────

/// <summary>
/// Thread-safe in-memory <see cref="IProvenanceStore"/> for testing and ephemeral sessions.
/// </summary>
public sealed class InMemoryProvenanceStore : IProvenanceStore
{
    private readonly List<ProvenanceRecord> _records = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public Task AppendAsync(ProvenanceRecord record, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _records.Add(record);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProvenanceRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ProvenanceRecord>>(_records.OrderBy(r => r.Timestamp).ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProvenanceRecord>> ReadByKindAsync(
        ProvenanceRecordKind kind,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ProvenanceRecord>>(
                _records.Where(r => r.Kind == kind).OrderBy(r => r.Timestamp).ToList());
    }
}
