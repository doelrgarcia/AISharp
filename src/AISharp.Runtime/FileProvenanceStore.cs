using System.Text.Json;
using System.Text.Json.Serialization;

namespace AISharp.Runtime;

// ─── JSON serialization support ───────────────────────────────────────────────

/// <summary>
/// Custom <see cref="JsonConverter{T}"/> for the <see cref="ProvenanceRecord"/> hierarchy.
/// Writes a <c>"kind"</c> discriminator property and round-trips all concrete subtypes.
/// Sensitive fields (raw prompts, API keys) are never written; records only contain hashes.
/// </summary>
public sealed class ProvenanceRecordConverter : JsonConverter<ProvenanceRecord>
{
    private static readonly JsonSerializerOptions _inner = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc/>
    public override ProvenanceRecord Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kindEl))
            throw new JsonException("Provenance record missing 'kind' discriminator.");

        var kind = Enum.Parse<ProvenanceRecordKind>(kindEl.GetString()!);
        var raw = root.GetRawText();

        return kind switch
        {
            ProvenanceRecordKind.ModelCall =>
                JsonSerializer.Deserialize<ModelCallProvenance>(raw, _inner)!,
            ProvenanceRecordKind.ToolInvocation =>
                JsonSerializer.Deserialize<ToolInvocationProvenance>(raw, _inner)!,
            ProvenanceRecordKind.SourceHash =>
                JsonSerializer.Deserialize<SourceHashProvenance>(raw, _inner)!,
            ProvenanceRecordKind.LoweredArtifact =>
                JsonSerializer.Deserialize<LoweredArtifactProvenance>(raw, _inner)!,
            ProvenanceRecordKind.BuildCommand =>
                JsonSerializer.Deserialize<BuildCommandProvenance>(raw, _inner)!,
            ProvenanceRecordKind.TestCommand =>
                JsonSerializer.Deserialize<TestCommandProvenance>(raw, _inner)!,
            ProvenanceRecordKind.HumanApproval =>
                JsonSerializer.Deserialize<HumanApprovalProvenance>(raw, _inner)!,
            ProvenanceRecordKind.PatchProposal =>
                JsonSerializer.Deserialize<PatchProposalProvenance>(raw, _inner)!,
            _ => throw new JsonException($"Unknown provenance kind '{kind}'.")
        };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ProvenanceRecord value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), _inner);
    }
}

/// <summary>
/// Stable JSON serialisation helpers for <see cref="ProvenanceRecord"/> values.
/// </summary>
public static class ProvenanceSerializer
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(),
            new ProvenanceRecordConverter()
        }
    };

    /// <summary>Serialises a provenance record to a single-line JSON string.</summary>
    public static string Serialize(ProvenanceRecord record) =>
        JsonSerializer.Serialize<ProvenanceRecord>(record, _options);

    /// <summary>Deserialises a provenance record from a JSON string.</summary>
    public static ProvenanceRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<ProvenanceRecord>(json, _options)
            ?? throw new JsonException("Deserialized provenance record was null.");
}

// ─── File-based provenance store ─────────────────────────────────────────────

/// <summary>
/// File-backed <see cref="IProvenanceStore"/> that writes NDJSON records to
/// <c>&lt;storeDirectory&gt;/provenance.ndjson</c>.
/// Safe for single-process use; uses an async reader/writer lock.
/// Does not store secrets or raw prompt content — only hashes and metadata.
/// </summary>
public sealed class FileProvenanceStore : IProvenanceStore, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Initialises the store at <paramref name="storeDirectory"/>.
    /// The directory is created if it does not exist.
    /// </summary>
    /// <param name="storeDirectory">
    /// Path to the directory where <c>provenance.ndjson</c> is written.
    /// Defaults to <c>.aisharp/provenance</c> relative to the current directory.
    /// </param>
    public FileProvenanceStore(string? storeDirectory = null)
    {
        var dir = storeDirectory
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".aisharp", "provenance");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "provenance.ndjson");
        StorePath = _filePath;
    }

    /// <summary>The absolute path to the NDJSON provenance file.</summary>
    public string StorePath { get; }

    /// <inheritdoc/>
    public async Task AppendAsync(ProvenanceRecord record,
        CancellationToken cancellationToken = default)
    {
        var line = ProvenanceSerializer.Serialize(record) + Environment.NewLine;
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_filePath, line, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProvenanceRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLinesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProvenanceRecord>> ReadByKindAsync(
        ProvenanceRecordKind kind,
        CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(r => r.Kind == kind).ToList();
    }

    /// <summary>
    /// Deletes the backing NDJSON file if it exists.
    /// Useful in tests to reset state between runs.
    /// </summary>
    public void Clear()
    {
        _semaphore.Wait();
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _semaphore.Dispose();

    private async Task<IReadOnlyList<ProvenanceRecord>> ReadLinesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];

        var lines = await File.ReadAllLinesAsync(_filePath, cancellationToken)
            .ConfigureAwait(false);
        var records = new List<ProvenanceRecord>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            try
            {
                records.Add(ProvenanceSerializer.Deserialize(trimmed));
            }
            catch (JsonException)
            {
                // Skip corrupt/unreadable lines without failing the whole store.
            }
        }

        return records.OrderBy(r => r.Timestamp).ToList();
    }
}
