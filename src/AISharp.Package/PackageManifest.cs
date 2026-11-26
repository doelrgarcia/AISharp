using System.Text.RegularExpressions;

namespace AISharp.Package;

// ─── Manifest model ───────────────────────────────────────────────────────────

/// <summary>Package section of an <c>aisharp.toml</c> manifest.</summary>
public sealed record PackageSection(
    string Name,
    string Version,
    string TargetFramework,
    IReadOnlyList<string> Sources);

/// <summary>Build options section of an <c>aisharp.toml</c> manifest.</summary>
public sealed record BuildSection(
    string? OutputDirectory,
    bool GenerateCSharp);

/// <summary>
/// Parsed representation of an <c>aisharp.toml</c> project manifest.
/// </summary>
public sealed record PackageManifest(
    PackageSection Package,
    string? DefaultPolicy,
    IReadOnlyDictionary<string, string> Dependencies,
    BuildSection Build)
{
    /// <summary>Path to the manifest file, or null when parsed from raw text.</summary>
    public string? ManifestPath { get; init; }
}

// ─── Manifest diagnostic ──────────────────────────────────────────────────────

/// <summary>Severity of a <see cref="ManifestDiagnostic"/>.</summary>
public enum ManifestDiagnosticSeverity { Error, Warning }

/// <summary>A diagnostic produced during manifest parsing or validation.</summary>
public sealed record ManifestDiagnostic(
    string Code,
    ManifestDiagnosticSeverity Severity,
    string Message,
    int Line);

// ─── Parse result ─────────────────────────────────────────────────────────────

/// <summary>Result of parsing an <c>aisharp.toml</c> file.</summary>
public sealed record ManifestParseResult(
    PackageManifest? Manifest,
    IReadOnlyList<ManifestDiagnostic> Diagnostics)
{
    /// <summary>True when parsing succeeded with no error-severity diagnostics.</summary>
    public bool Success => Diagnostics.All(d => d.Severity != ManifestDiagnosticSeverity.Error);
}

// ─── Manifest parser ──────────────────────────────────────────────────────────

/// <summary>
/// Simple parser for the <c>aisharp.toml</c> package manifest format.
/// Supports table headers (<c>[section]</c>), key = value pairs, and inline arrays.
/// </summary>
public static class ManifestParser
{
    private static readonly char[] TrimChars = [' ', '\t', '"', '\''];

    /// <summary>
    /// Parses an <c>aisharp.toml</c> manifest from the given text.
    /// </summary>
    /// <param name="text">Raw TOML-like manifest content.</param>
    /// <param name="manifestPath">Optional file path, stored on the result for diagnostics.</param>
    public static ManifestParseResult Parse(string text, string? manifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var diagnostics = new List<ManifestDiagnostic>();
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;

        var lines = text.Split(['\r', '\n']);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var raw = lines[i];
            var line = StripComment(raw).Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim().ToLowerInvariant();
                if (!sections.ContainsKey(currentSection))
                    sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eqIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex < 0)
            {
                diagnostics.Add(new ManifestDiagnostic(
                    "PM0001", ManifestDiagnosticSeverity.Error,
                    $"Expected 'key = value' but found: {raw.Trim()}", lineNumber));
                continue;
            }

            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();

            if (string.IsNullOrEmpty(key))
            {
                diagnostics.Add(new ManifestDiagnostic(
                    "PM0002", ManifestDiagnosticSeverity.Error,
                    "Empty key in manifest.", lineNumber));
                continue;
            }

            if (!sections.TryGetValue(currentSection, out var sectionMap))
            {
                sectionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[currentSection] = sectionMap;
            }

            if (sectionMap.ContainsKey(key))
            {
                diagnostics.Add(new ManifestDiagnostic(
                    "PM0003", ManifestDiagnosticSeverity.Warning,
                    $"Duplicate key '{key}' in [{currentSection}].", lineNumber));
            }

            sectionMap[key] = value;
        }

        // Validate required [package] section.
        if (!sections.TryGetValue("package", out var pkg))
        {
            diagnostics.Add(new ManifestDiagnostic(
                "PM0010", ManifestDiagnosticSeverity.Error,
                "Missing required [package] section.", 0));
            return new ManifestParseResult(null, diagnostics);
        }

        if (!pkg.TryGetValue("name", out var nameRaw) || string.IsNullOrWhiteSpace(nameRaw))
        {
            diagnostics.Add(new ManifestDiagnostic(
                "PM0011", ManifestDiagnosticSeverity.Error,
                "Missing required field 'name' in [package].", 0));
        }

        if (!pkg.TryGetValue("version", out var versionRaw) || string.IsNullOrWhiteSpace(versionRaw))
        {
            diagnostics.Add(new ManifestDiagnostic(
                "PM0012", ManifestDiagnosticSeverity.Error,
                "Missing required field 'version' in [package].", 0));
        }

        if (diagnostics.Any(d => d.Severity == ManifestDiagnosticSeverity.Error))
            return new ManifestParseResult(null, diagnostics);

        var name = nameRaw!.Trim(TrimChars);
        var version = versionRaw!.Trim(TrimChars);
        var target = pkg.TryGetValue("target", out var targetRaw)
            ? targetRaw.Trim(TrimChars)
            : "net9.0";
        var sources = pkg.TryGetValue("sources", out var sourcesRaw)
            ? ParseArray(sourcesRaw)
            : ["*.aisharp"];

        // Validate package name (no spaces, reasonable chars).
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9._\-]+$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            diagnostics.Add(new ManifestDiagnostic(
                "PM0013", ManifestDiagnosticSeverity.Error,
                $"Invalid package name '{name}'. Use alphanumeric characters, dots, underscores, or hyphens.", 0));
        }

        // Validate version (basic semver pattern).
        if (!Regex.IsMatch(version, @"^\d+\.\d+(\.\d+)?(-[a-zA-Z0-9.]+)?$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            diagnostics.Add(new ManifestDiagnostic(
                "PM0014", ManifestDiagnosticSeverity.Warning,
                $"Version '{version}' does not match expected semver format (major.minor.patch).", 0));
        }

        if (diagnostics.Any(d => d.Severity == ManifestDiagnosticSeverity.Error))
            return new ManifestParseResult(null, diagnostics);

        // Policy section.
        string? defaultPolicy = null;
        if (sections.TryGetValue("policy", out var policySection) &&
            policySection.TryGetValue("default", out var policyRaw))
        {
            defaultPolicy = policyRaw.Trim(TrimChars);
        }

        // Dependencies section.
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sections.TryGetValue("dependencies", out var depsSection))
        {
            foreach (var (k, v) in depsSection)
                dependencies[k] = v.Trim(TrimChars);
        }

        // Build section.
        string? outputDir = null;
        var generateCSharp = true;
        if (sections.TryGetValue("build", out var buildSection))
        {
            if (buildSection.TryGetValue("output", out var outRaw))
                outputDir = outRaw.Trim(TrimChars);
            if (buildSection.TryGetValue("generate_csharp", out var genRaw))
                generateCSharp = !string.Equals(genRaw.Trim(TrimChars), "false", StringComparison.OrdinalIgnoreCase);
        }

        var manifest = new PackageManifest(
            Package: new PackageSection(name, version, target, sources),
            DefaultPolicy: defaultPolicy,
            Dependencies: dependencies,
            Build: new BuildSection(outputDir, generateCSharp))
        {
            ManifestPath = manifestPath
        };

        return new ManifestParseResult(manifest, diagnostics);
    }

    /// <summary>
    /// Parses an inline TOML array value such as <c>["a", "b", "c"]</c> or <c>[a, b]</c>.
    /// Returns an empty list if the value is not an array.
    /// </summary>
    public static IReadOnlyList<string> ParseArray(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            return [trimmed.Trim(TrimChars)];

        var inner = trimmed[1..^1];
        if (string.IsNullOrWhiteSpace(inner))
            return [];

        return inner.Split(',')
            .Select(s => s.Trim(TrimChars))
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string StripComment(string line)
    {
        // Strip inline # comments that are not inside quotes.
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (ch == '#' && !inSingleQuote && !inDoubleQuote)
                return line[..i];
        }

        return line;
    }
}

// ─── Manifest locator ─────────────────────────────────────────────────────────

/// <summary>
/// Locates the nearest <c>aisharp.toml</c> manifest file by walking up the directory tree.
/// </summary>
public static class ManifestLocator
{
    private const string ManifestFileName = "aisharp.toml";

    /// <summary>
    /// Searches for <c>aisharp.toml</c> starting at <paramref name="startDirectory"/> and
    /// walking up to the filesystem root. Returns the first manifest found, or null.
    /// </summary>
    public static string? FindManifest(string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        var dir = Path.GetFullPath(startDirectory);
        while (true)
        {
            var candidate = Path.Combine(dir, ManifestFileName);
            if (File.Exists(candidate))
                return candidate;

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                break;

            dir = parent;
        }

        return null;
    }

    /// <summary>
    /// Reads and parses the manifest at the given path.
    /// </summary>
    public static ManifestParseResult LoadManifest(string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        var text = File.ReadAllText(manifestPath);
        return ManifestParser.Parse(text, manifestPath);
    }
}
