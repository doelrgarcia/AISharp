using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AISharp.Runtime;

// ─── Release hash manifest ────────────────────────────────────────────────────

/// <summary>A single file entry in a release hash manifest.</summary>
public sealed record ReleaseHashEntry(
    string RelativePath,
    string Algorithm,
    string Hash,
    long SizeBytes);

/// <summary>
/// Manifest of content-addressed hashes for a set of release artifacts.
/// Written as <c>release-hashes.json</c> during the release command.
/// </summary>
public sealed record ReleaseHashManifest(
    string PackageName,
    string PackageVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ReleaseHashEntry> Entries)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes this manifest to a JSON string.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes a hash manifest from JSON.</summary>
    public static ReleaseHashManifest? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<ReleaseHashManifest>(json, JsonOptions);
    }

    /// <summary>
    /// Builds a <see cref="ReleaseHashManifest"/> by hashing each file in <paramref name="artifactPaths"/>.
    /// </summary>
    public static ReleaseHashManifest Create(
        string packageName,
        string packageVersion,
        string baseDirectory,
        IEnumerable<string> artifactPaths)
    {
        ArgumentNullException.ThrowIfNull(packageName);
        ArgumentNullException.ThrowIfNull(packageVersion);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(artifactPaths);

        var entries = artifactPaths
            .Select(path =>
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(baseDirectory, path)
                    .Replace('\\', '/');
                var hash = ComputeSha256(path);
                return new ReleaseHashEntry(relative, "sha256", hash, info.Length);
            })
            .OrderBy(e => e.RelativePath, StringComparer.Ordinal)
            .ToList();

        return new ReleaseHashManifest(packageName, packageVersion, DateTimeOffset.UtcNow, entries);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hashBytes);
    }
}

// ─── SBOM model ───────────────────────────────────────────────────────────────

/// <summary>A single component entry in the AI# SBOM.</summary>
public sealed record SbomComponent(
    string Name,
    string Version,
    string Type,
    string? LicenseExpression,
    string? SourceUrl);

/// <summary>
/// Minimal software bill of materials (SBOM) for an AI# release.
/// A full SPDX/CycloneDX SBOM can be generated from this structure by post-processing.
/// </summary>
public sealed record Releasesbom(
    string PackageName,
    string PackageVersion,
    string SbomFormat,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SbomComponent> Components)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The SBOM format identifier used by this implementation.</summary>
    public const string AiSharpMinimalFormat = "aisharp-sbom-v1";

    /// <summary>Serializes this SBOM to a JSON string.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes an SBOM from JSON.</summary>
    public static Releasesbom? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<Releasesbom>(json, JsonOptions);
    }

    /// <summary>
    /// Builds a minimal SBOM from the known AI# components.
    /// </summary>
    public static Releasesbom CreateMinimal(string packageName, string packageVersion)
    {
        var components = new[]
        {
            new SbomComponent("AISharp.Compiler",       packageVersion, "library", "Apache-2.0", null),
            new SbomComponent("AISharp.Lowering",       packageVersion, "library", "Apache-2.0", null),
            new SbomComponent("AISharp.Runtime",        packageVersion, "library", "Apache-2.0", null),
            new SbomComponent("AISharp.LanguageServer", packageVersion, "library", "Apache-2.0", null),
            new SbomComponent("AISharp.Package",        packageVersion, "library", "Apache-2.0", null),
            new SbomComponent("AISharp.Cli",            packageVersion, "tool",    "Apache-2.0", null),
        };

        return new Releasesbom(packageName, packageVersion, AiSharpMinimalFormat, DateTimeOffset.UtcNow, components);
    }
}

// ─── Release provenance ───────────────────────────────────────────────────────

/// <summary>Gate result within a release provenance record.</summary>
public sealed record ReleaseGateResult(
    string Name,
    bool Passed,
    string? Detail);

/// <summary>
/// Provenance record for an AI# release operation.
/// Captures build command, test evidence, artifact hashes, SBOM path, and signing state.
/// </summary>
public sealed record ReleaseProvenanceRecord(
    string PackageName,
    string PackageVersion,
    string? SourceCommit,
    string BuildCommand,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> PackagePaths,
    string? HashManifestPath,
    string? SbomPath,
    string? SignaturePath,
    bool IsDryRun,
    IReadOnlyList<ReleaseGateResult> Gates)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>True when all release gates passed.</summary>
    public bool AllGatesPassed => Gates.All(g => g.Passed);

    /// <summary>Serializes this record to JSON.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes a release provenance record from JSON.</summary>
    public static ReleaseProvenanceRecord? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<ReleaseProvenanceRecord>(json, JsonOptions);
    }
}
