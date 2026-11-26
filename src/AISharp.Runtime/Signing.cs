using System.Text.Json;
using System.Text.Json.Serialization;

namespace AISharp.Runtime;

// ─── Signing configuration ────────────────────────────────────────────────────

/// <summary>The signing provider kind used for a release.</summary>
public enum SigningProviderKind
{
    /// <summary>No signing provider configured — dry-run only.</summary>
    None,
    /// <summary>External signing provider integration point (e.g., Azure Key Vault, Sigstore).</summary>
    External
}

/// <summary>
/// Configuration for the release signing step.
/// Signing configuration must never include private keys or credentials in this model.
/// Keys are always external to this record; only references (hints, output paths) are stored.
/// </summary>
public sealed record SigningConfiguration(
    SigningProviderKind Provider,
    string? ExternalProviderHint,
    bool DryRun,
    string? SignatureOutputPath = null)
{
    /// <summary>Returns a dry-run-only signing configuration with no provider.</summary>
    public static SigningConfiguration DryRunOnly { get; } = new(SigningProviderKind.None, null, true, null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // Field names treated as secret-like and rejected when present in a config file.
    private static readonly string[] ForbiddenSecretFieldNames =
    [
        "privateKey", "private_key", "key", "secret", "password", "pfxPassword",
        "clientSecret", "client_secret", "apiKey", "api_key", "token", "accessToken",
        "certificateContent", "pfx"
    ];

    /// <summary>Serializes the configuration to JSON. Never contains secrets.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Loads a signing configuration from a JSON file. Returns <c>null</c> when the file does not exist.
    /// Throws <see cref="InvalidOperationException"/> if the file contains any forbidden secret-like fields.
    /// </summary>
    public static SigningConfiguration? LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    /// <summary>
    /// Loads a signing configuration from a JSON string.
    /// Throws <see cref="InvalidOperationException"/> if the JSON contains any forbidden secret-like fields.
    /// </summary>
    public static SigningConfiguration LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Signing configuration JSON must be an object.");
        }

        foreach (var property in root.EnumerateObject())
        {
            foreach (var forbidden in ForbiddenSecretFieldNames)
            {
                if (string.Equals(property.Name, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Signing configuration must not contain secret-like field '{property.Name}'. " +
                        "Keys and credentials must be supplied to the external signing provider out-of-band.");
                }
            }
        }

        var config = JsonSerializer.Deserialize<SigningConfiguration>(json, JsonOptions)
            ?? throw new InvalidOperationException("Signing configuration JSON could not be deserialized.");
        return config;
    }
}

// ─── Signing diagnostic ───────────────────────────────────────────────────────

/// <summary>Severity of a signing diagnostic.</summary>
public enum SigningDiagnosticSeverity { Error, Warning, Info }

/// <summary>A diagnostic produced during the signing dry-run or signing operation.</summary>
public sealed record SigningDiagnostic(
    string Code,
    SigningDiagnosticSeverity Severity,
    string Message);

// ─── Signing result ───────────────────────────────────────────────────────────

/// <summary>
/// Result of a signing dry-run or actual signing operation.
/// </summary>
public sealed record SigningResult(
    bool Success,
    bool IsDryRun,
    SigningProviderKind Provider,
    IReadOnlyList<string> ArtifactPaths,
    string? SignatureArtifactPath,
    IReadOnlyList<SigningDiagnostic> Diagnostics)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes this result to JSON.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
}

// ─── Signing service ──────────────────────────────────────────────────────────

/// <summary>
/// Performs dry-run artifact validation and provides an integration point for real signing providers.
/// No private keys or secrets must ever be passed into this service.
/// </summary>
public static class ReleaseSigningService
{
    /// <summary>Signing diagnostic code for missing configuration.</summary>
    public const string DiagMissingConfig = "SN0001";

    /// <summary>Signing diagnostic code for a dry-run note.</summary>
    public const string DiagDryRunNote = "SN0002";

    /// <summary>Signing diagnostic code for a missing artifact.</summary>
    public const string DiagMissingArtifact = "SN0003";

    /// <summary>
    /// Performs a dry-run signing validation: verifies configuration is present,
    /// verifies artifact files exist, and records a <see cref="SigningResult"/> with
    /// no actual signing performed.
    /// </summary>
    public static SigningResult DryRun(
        SigningConfiguration config,
        IEnumerable<string> artifactPaths,
        string? signatureOutputPath = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(artifactPaths);

        var diagnostics = new List<SigningDiagnostic>();
        var paths = artifactPaths.ToList();

        if (config.Provider == SigningProviderKind.None)
        {
            diagnostics.Add(new SigningDiagnostic(
                DiagMissingConfig,
                SigningDiagnosticSeverity.Warning,
                "No signing provider configured. Set up an external signing provider before production release."));
        }

        diagnostics.Add(new SigningDiagnostic(
            DiagDryRunNote,
            SigningDiagnosticSeverity.Info,
            "Signing dry-run: no artifacts were signed. Verify artifact list and configure provider before release."));

        if (paths.Count == 0)
        {
            diagnostics.Add(new SigningDiagnostic(
                DiagMissingArtifact,
                SigningDiagnosticSeverity.Error,
                "No artifacts were provided to the signing dry-run."));
        }

        var missingPaths = paths.Where(p => !File.Exists(p)).ToList();
        foreach (var missing in missingPaths)
        {
            diagnostics.Add(new SigningDiagnostic(
                DiagMissingArtifact,
                SigningDiagnosticSeverity.Error,
                $"Artifact does not exist: {missing}"));
        }

        var success = paths.Count > 0 && missingPaths.Count == 0;

        var resolvedSignaturePath = signatureOutputPath ?? config.SignatureOutputPath;

        return new SigningResult(
            Success: success,
            IsDryRun: true,
            Provider: config.Provider,
            ArtifactPaths: paths,
            SignatureArtifactPath: resolvedSignaturePath,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Returns a clear, single-line diagnostic message for missing or empty signing configuration.
    /// Used by CLI surfaces that need to render a deterministic message.
    /// </summary>
    public static string FormatMissingConfigurationDiagnostic(string? configPath) =>
        string.IsNullOrWhiteSpace(configPath)
            ? "Signing configuration missing. Provide --config <path> or accept the dry-run-only default (no provider)."
            : $"Signing configuration not found at '{configPath}'. Provide a valid JSON file that names an external signing provider (no secrets).";
}
