using System.Text.Json;
using System.Text.Json.Serialization;

namespace AISharp.Package;

// ─── Lockfile entry ───────────────────────────────────────────────────────────

/// <summary>A resolved dependency entry in the lockfile.</summary>
public sealed record LockfileEntry(
    string Name,
    string ResolvedVersion,
    string? SourceUrl,
    string? IntegrityHash);

// ─── Lockfile model ───────────────────────────────────────────────────────────

/// <summary>
/// Content-addressable lockfile that records resolved dependency versions.
/// Stored as <c>aisharp.lock.json</c> alongside <c>aisharp.toml</c>.
/// </summary>
public sealed record PackageLockfile(
    string LockfileVersion,
    string PackageName,
    string PackageVersion,
    IReadOnlyList<LockfileEntry> Dependencies,
    DateTimeOffset ResolvedAt)
{
    /// <summary>Current lockfile format version.</summary>
    public const string CurrentVersion = "1";

    /// <summary>Default lockfile file name.</summary>
    public const string FileName = "aisharp.lock.json";

    /// <summary>Creates a lockfile for the given manifest with the supplied resolved entries.</summary>
    public static PackageLockfile Create(
        PackageManifest manifest,
        IEnumerable<LockfileEntry> resolvedDependencies)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(resolvedDependencies);

        return new PackageLockfile(
            LockfileVersion: CurrentVersion,
            PackageName: manifest.Package.Name,
            PackageVersion: manifest.Package.Version,
            Dependencies: [.. resolvedDependencies],
            ResolvedAt: DateTimeOffset.UtcNow);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes this lockfile to a JSON string.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes a lockfile from a JSON string.</summary>
    public static PackageLockfile? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<PackageLockfile>(json, JsonOptions);
    }

    /// <summary>
    /// Writes the lockfile to <c>aisharp.lock.json</c> next to the manifest.
    /// </summary>
    public void Save(string manifestDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifestDirectory);
        var path = Path.Combine(manifestDirectory, FileName);
        File.WriteAllText(path, Serialize());
    }

    /// <summary>
    /// Loads and deserializes the lockfile from the given directory, or returns null if not found.
    /// </summary>
    public static PackageLockfile? Load(string manifestDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifestDirectory);
        var path = Path.Combine(manifestDirectory, FileName);
        if (!File.Exists(path))
            return null;

        return Deserialize(File.ReadAllText(path));
    }
}
