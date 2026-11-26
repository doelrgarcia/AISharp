using AISharp.Compiler;
using AISharp.Lowering;
using AISharp.Runtime;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length > 0 && args[0] == "build-native")
    return NativeBuildCommand.Run(args[1..]);

const int ExitSuccess = 0;
const int ExitCommandFailure = 1;
const int ExitDiagnostics = 2;
const int ExitLoweringOrBuildFailure = 3;
const int ExitPolicyOrSecurityFailure = 4;
const string SkipCliGateTestsVariable = "AISHARP_SKIP_CLI_GATE_TESTS";
const int ChildProcessTimeoutMilliseconds = 300_000;

args = NormalizeLanguageProfileArguments(args);
var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
var commandStart = Stopwatch.StartNew();

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return args.Length == 0 ? ExitCommandFailure : ExitSuccess;
}

try
{
    var command = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal))?.ToLowerInvariant();
    if (command is null)
    {
        var missingCommandResult = Failure("unknown", "No command or source file was provided.", ExitCommandFailure, commandStart);
        if (json)
            Console.WriteLine(SerializeJson(missingCommandResult.Payload));
        else
            Console.Error.WriteLine("No command or source file was provided.");

        return missingCommandResult.ExitCode;
    }

    var explicitCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "validate",
        "lower",
        "build",
        "run-sample",
        "format",
        "audit",
        "release",
        "sign",
        "acceptance"
    };

    if (!explicitCommands.Contains(command))
    {
        return RunLegacyValidate(args, json);
    }

    var result = command switch
    {
        "validate" => RunValidateCommand(args, commandStart),
        "lower" => RunLowerCommand(args, commandStart),
        "build" => RunBuildCommand(args, commandStart),
        "run-sample" => RunSampleCommand(args, commandStart),
        "format" => RunFormatCommand(args, commandStart),
        "audit" => RunAuditCommand(args, commandStart),
        "release" => RunReleaseCommand(args, commandStart),
        "sign" => RunSignCommand(args, commandStart),
        "acceptance" => RunAcceptanceCommand(args, commandStart),
        _ => Failure(command, $"Unknown command '{command}'.", ExitCommandFailure, commandStart)
    };

    if (json)
    {
        Console.WriteLine(SerializeJson(result.Payload));
    }
    else
    {
        WriteHumanResult(result);
    }

    return result.ExitCode;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    var command = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal)) ?? "unknown";
    var result = Failure(command, ex.Message, ExitCommandFailure, commandStart);
    if (json)
        Console.WriteLine(SerializeJson(result.Payload));
    else
        Console.Error.WriteLine(ex.Message);

    return result.ExitCode;
}
catch (Exception ex)
{
    var command = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal)) ?? "unknown";
    var result = Failure(command, $"Runtime failure: {ex.Message}", ExitCommandFailure, commandStart);
    if (json)
        Console.WriteLine(SerializeJson(result.Payload));
    else
        Console.Error.WriteLine($"Runtime failure: {ex.Message}");

    return result.ExitCode;
}

static CommandResult RunValidateCommand(string[] args, Stopwatch stopwatch)
{
    if (!TryGetPositional(args, 1, out var path, out var error))
        return Failure("validate", error, ExitCommandFailure, stopwatch);

    var compilation = Compile(path);
    var exitCode = ExitCodeForCompilation(compilation);
    var artifacts = new[] { new ArtifactInfo("source", Path.GetFullPath(path)) };
    return FromCompilation("validate", compilation, artifacts, exitCode, stopwatch);
}

static CommandResult RunLowerCommand(string[] args, Stopwatch stopwatch)
{
    if (!TryGetPositional(args, 1, out var path, out var error))
        return Failure("lower", error, ExitCommandFailure, stopwatch);

    var outDir = GetOptionValue(args, "--out");
    if (string.IsNullOrWhiteSpace(outDir))
        return Failure("lower", "Missing required option: --out <dir>.", ExitCommandFailure, stopwatch);

    var compilation = Compile(path);
    var compileExit = ExitCodeForCompilation(compilation);
    if (compileExit != ExitSuccess)
        return FromCompilation("lower", compilation, [], compileExit, stopwatch);

    try
    {
        Directory.CreateDirectory(outDir);
        var lowerResult = AISharpLowering.Lower(compilation.MirResult.Document);
        var generatedPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(path)}.g.cs");
        File.WriteAllText(generatedPath, lowerResult.GeneratedCSharpSource);

        var sourceMapPath = Path.ChangeExtension(generatedPath, ".sourcemap.json");
        var sourceMap = lowerResult.SourceMap.ToDictionary(
            item => item.Key,
            item => ToJsonLocation(item.Value, path),
            StringComparer.Ordinal);
        File.WriteAllText(sourceMapPath, SerializeJson(sourceMap));

        var artifacts = new[]
        {
            new ArtifactInfo("csharp", Path.GetFullPath(generatedPath)),
            new ArtifactInfo("source-map", Path.GetFullPath(sourceMapPath))
        };

        // Write lowered-artifact provenance record.
        var provenancePath = WriteProvenanceRecord(outDir, new LoweredArtifactProvenance(
            DateTimeOffset.UtcNow,
            ArtifactPath: Path.GetFullPath(generatedPath),
            Hash: ComputeFileHash(generatedPath),
            SourcePath: Path.GetFullPath(path)));

        var extra = provenancePath is not null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
                { ["provenancePath"] = provenancePath }
            : null;

        return FromCompilation("lower", compilation, artifacts, ExitSuccess, stopwatch, extra);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Failure("lower", $"Lowering failed: {ex.Message}", ExitLoweringOrBuildFailure, stopwatch);
    }
}

static CommandResult RunBuildCommand(string[] args, Stopwatch stopwatch)
{
    if (!TryGetPositional(args, 1, out var path, out var error))
        return Failure("build", error, ExitCommandFailure, stopwatch);

    if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
        return RunDotnetBuildProject("build", path, stopwatch);

    if (Directory.Exists(path))
    {
        var projects = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length == 1)
            return RunDotnetBuildProject("build", projects[0], stopwatch);

        if (projects.Length > 1)
            return Failure("build", $"Multiple .csproj files were found in directory '{path}'. Pass a specific .csproj path.", ExitCommandFailure, stopwatch);

        var aisharpFiles = Directory.GetFiles(path, "*.aisharp", SearchOption.TopDirectoryOnly);
        if (aisharpFiles.Length > 0)
            return RunBuildAISharpPackage(path, aisharpFiles, stopwatch);

        return Failure("build", $"No .csproj or .aisharp files were found in directory: {path}", ExitCommandFailure, stopwatch);
    }

    if (!File.Exists(path))
        return Failure("build", $"Build input not found: {path}", ExitCommandFailure, stopwatch);

    var compilation = Compile(path);
    var compileExit = ExitCodeForCompilation(compilation);
    if (compileExit != ExitSuccess)
        return FromCompilation("build", compilation, [], compileExit, stopwatch);

    var buildDir = Path.Combine(
        Directory.GetCurrentDirectory(),
        "artifacts",
        "build",
        $"{SanitizeFileName(Path.GetFileNameWithoutExtension(path))}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
    Directory.CreateDirectory(buildDir);

    try
    {
        var lowerResult = AISharpLowering.Lower(compilation.MirResult.Document);
        var generatedPath = Path.Combine(buildDir, $"{Path.GetFileNameWithoutExtension(path)}.g.cs");
        File.WriteAllText(generatedPath, lowerResult.GeneratedCSharpSource);
        var projectPath = Path.Combine(buildDir, "aisharp-generated.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <OutputType>Library</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        return RunDotnetBuildProject("build", projectPath, stopwatch, [
            new ArtifactInfo("csharp", Path.GetFullPath(generatedPath))
        ]);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Failure("build", $"Build preparation failed: {ex.Message}", ExitLoweringOrBuildFailure, stopwatch);
    }
}

// ─── PR-37 Multi-file AI# package build ──────────────────────────────────────
// Compiles every .aisharp source in <paramref name="directory"/>, aggregates
// diagnostics, lowers each module to its own .g.cs, and runs `dotnet build`
// against a generated .csproj. When an `aisharp.toml` manifest is present, its
// [package] section drives the package name and target framework.
static CommandResult RunBuildAISharpPackage(
    string directory,
    string[] aisharpFiles,
    Stopwatch stopwatch)
{
    var orderedFiles = aisharpFiles
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var packageName = SanitizeFileName(Path.GetFileName(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))));
    if (string.IsNullOrWhiteSpace(packageName))
        packageName = "aisharp_package";
    var targetFramework = "net9.0";

    var manifestPath = Path.Combine(directory, "aisharp.toml");
    var manifestDiagnostics = new List<object>();
    if (File.Exists(manifestPath))
    {
        try
        {
            var manifestResult = AISharp.Package.ManifestLocator.LoadManifest(manifestPath);
            foreach (var diag in manifestResult.Diagnostics)
            {
                manifestDiagnostics.Add(new
                {
                    code = diag.Code,
                    severity = diag.Severity.ToString().ToLowerInvariant(),
                    message = diag.Message,
                    location = new
                    {
                        path = Path.GetFullPath(manifestPath),
                        line = diag.Line,
                        column = 1,
                        offset = 0
                    }
                });
            }
            if (manifestResult.Manifest is { } manifest)
            {
                if (!string.IsNullOrWhiteSpace(manifest.Package.Name))
                    packageName = SanitizeFileName(manifest.Package.Name);
                if (!string.IsNullOrWhiteSpace(manifest.Package.TargetFramework))
                    targetFramework = manifest.Package.TargetFramework;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure("build", $"Failed to read manifest '{manifestPath}': {ex.Message}", ExitCommandFailure, stopwatch);
        }
    }

    var compilations = new List<CompilationResult>(orderedFiles.Length);
    var aggregatedDiagnostics = new List<object>(manifestDiagnostics);
    var worstExit = ExitSuccess;

    foreach (var file in orderedFiles)
    {
        CompilationResult compilation;
        try
        {
            compilation = Compile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure("build", $"Failed to read AI# source '{file}': {ex.Message}", ExitCommandFailure, stopwatch);
        }
        compilations.Add(compilation);

        foreach (var diag in AllDiagnostics(compilation))
            aggregatedDiagnostics.Add(ToJsonDiagnostic(diag, compilation.SourcePath));

        var fileExit = ExitCodeForCompilation(compilation);
        if (fileExit == ExitPolicyOrSecurityFailure)
            worstExit = ExitPolicyOrSecurityFailure;
        else if (fileExit == ExitDiagnostics && worstExit != ExitPolicyOrSecurityFailure)
            worstExit = ExitDiagnostics;
    }

    var hasManifestError = manifestDiagnostics.Any(d =>
        d is not null &&
        d.GetType().GetProperty("severity")?.GetValue(d) as string == "error");
    if (hasManifestError && worstExit == ExitSuccess)
        worstExit = ExitCommandFailure;

    if (worstExit != ExitSuccess)
    {
        var sourceArtifacts = orderedFiles
            .Select(f => new ArtifactInfo("source", Path.GetFullPath(f)))
            .ToArray();
        var failPayload = CreateResultPayload(
            "build",
            success: false,
            aggregatedDiagnostics,
            sourceArtifacts,
            stopwatch,
            new Dictionary<string, object?>
            {
                ["package"] = new
                {
                    name = packageName,
                    targetFramework,
                    sources = orderedFiles.Select(Path.GetFullPath).ToArray(),
                    manifestPath = File.Exists(manifestPath) ? Path.GetFullPath(manifestPath) : null
                }
            });
        return new CommandResult(worstExit, failPayload);
    }

    var buildDir = Path.Combine(
        Directory.GetCurrentDirectory(),
        "artifacts",
        "build",
        $"{SanitizeFileName(packageName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");

    try
    {
        Directory.CreateDirectory(buildDir);

        var generatedArtifacts = new List<ArtifactInfo>();
        var generatedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilation in compilations)
        {
            var lowered = AISharpLowering.Lower(compilation.MirResult.Document);
            var baseName = Path.GetFileNameWithoutExtension(compilation.SourcePath);
            var generatedName = $"{baseName}.g.cs";
            // Ensure unique generated names even if two AI# files share a base name.
            var disambiguator = 1;
            while (!generatedFileNames.Add(generatedName))
            {
                generatedName = $"{baseName}.{disambiguator++}.g.cs";
            }
            var generatedPath = Path.Combine(buildDir, generatedName);
            File.WriteAllText(generatedPath, lowered.GeneratedCSharpSource);
            generatedArtifacts.Add(new ArtifactInfo("csharp", Path.GetFullPath(generatedPath)));
        }

        var projectPath = Path.Combine(buildDir, $"{packageName}.csproj");
        File.WriteAllText(projectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <OutputType>Library</OutputType>
                <AssemblyName>{packageName}</AssemblyName>
                <RootNamespace>{packageName}</RootNamespace>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                <UseSharedCompilation>false</UseSharedCompilation>
                <Deterministic>true</Deterministic>
              </PropertyGroup>
            </Project>
            """);

        foreach (var file in orderedFiles)
            generatedArtifacts.Add(new ArtifactInfo("source", Path.GetFullPath(file)));
        if (File.Exists(manifestPath))
            generatedArtifacts.Add(new ArtifactInfo("manifest", Path.GetFullPath(manifestPath)));

        return RunDotnetBuildProject("build", projectPath, stopwatch, generatedArtifacts);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Failure("build", $"Build preparation failed: {ex.Message}", ExitLoweringOrBuildFailure, stopwatch);
    }
}

static CommandResult RunSampleCommand(string[] args, Stopwatch stopwatch)
{
    if (!TryGetPositional(args, 1, out var sampleName, out var error))
        return Failure("run-sample", error, ExitCommandFailure, stopwatch);

    if (string.Equals(sampleName, "ai-patch", StringComparison.OrdinalIgnoreCase))
        return RunAiPatchSample(args, stopwatch);

    var fileName = sampleName.EndsWith(".aisharp", StringComparison.OrdinalIgnoreCase)
        ? sampleName
        : $"{sampleName}.aisharp";
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", fileName);
    if (!File.Exists(samplePath))
        return Failure("run-sample", $"Sample not found: {sampleName}", ExitCommandFailure, stopwatch);

    var compilation = Compile(samplePath);
    var exitCode = ExitCodeForCompilation(compilation);
    var artifacts = new[] { new ArtifactInfo("source", Path.GetFullPath(samplePath)) };
    return FromCompilation("run-sample", compilation, artifacts, exitCode, stopwatch);
}

// ─── PR-38 AI patch workflow sample ──────────────────────────────────────────
//
// Demonstrates the controlled AI-assisted patch proposal pipeline:
//   diagnostic -> patch proposal (fake local model) -> schema validation ->
//   structural validation -> provenance record.
//
// Runs entirely offline. Never mutates the source `.aisharp` file. When
// `--apply` is supplied the patched content is written to a copy under the
// artifacts directory; the original source is left untouched and its hash is
// re-checked at the end so we can prove non-mutation in the JSON output.

static CommandResult RunAiPatchSample(string[] args, Stopwatch stopwatch)
{
    const string Command = "run-sample";
    var repoRoot = FindRepositoryRoot();
    var samplePath = Path.Combine(repoRoot, "samples", "ai-patch", "buggy.aisharp");

    if (!File.Exists(samplePath))
    {
        return Failure(Command,
            $"AI patch sample source not found: {samplePath}",
            ExitCommandFailure, stopwatch);
    }

    var outRoot = GetOptionValue(args, "--out")
        ?? Path.Combine(repoRoot, "artifacts", "samples", "ai-patch");
    var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);

    string sourceHashBefore;
    try
    {
        Directory.CreateDirectory(outRoot);
        sourceHashBefore = ComputeFileHash(samplePath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Failure(Command,
            $"Failed to prepare AI patch sample output: {ex.Message}",
            ExitCommandFailure, stopwatch);
    }

    // 1. Compile the buggy file so any real compiler diagnostics flow through
    //    the standard JSON shape. The sample tolerates either an empty or a
    //    non-empty diagnostic list — the AI-targeted diagnostic is synthetic.
    CompilationResult compilation;
    try
    {
        compilation = Compile(samplePath);
    }
    catch (Exception ex) when (ex is IOException or FileNotFoundException)
    {
        return Failure(Command,
            $"Failed to compile AI patch sample: {ex.Message}",
            ExitCommandFailure, stopwatch);
    }

    var aiDiagnostic = new
    {
        code = "AI4001",
        severity = "info",
        message = "discountFor always returns 0; VIP customers should receive a non-zero discount.",
        location = new
        {
            path = samplePath,
            line = 8
        }
    };

    // 2. Run the patch-proposal helper with a deterministic fake local model.
    PatchProposalResult proposalResult;
    string proposalJson;
    string provenanceStorePath;
    string proposalArtifactPath = Path.Combine(outRoot, "patch-proposal.json");

    try
    {
        const string fixedSource =
            "module orders.discount\n\n" +
            "intent \"Compute a customer discount; VIP customers receive a 10 percent discount\"\n\n" +
            "fn discountFor(customer: string) -> int\n" +
            "    requires customer != \"\"\n" +
            "    ensures true\n" +
            "{\n" +
            "    let discount = if customer == \"vip\" then 10 else 0\n" +
            "    return discount\n" +
            "}\n";

        const string unifiedDiff =
            "--- a/samples/ai-patch/buggy.aisharp\n" +
            "+++ b/samples/ai-patch/buggy.aisharp\n" +
            "@@\n" +
            "-intent \"Compute a customer discount; current implementation always returns zero for VIP customers\"\n" +
            "+intent \"Compute a customer discount; VIP customers receive a 10 percent discount\"\n" +
            "@@\n" +
            "-    let discount = 0\n" +
            "+    let discount = if customer == \"vip\" then 10 else 0\n";

        proposalJson = """
            {
              "changedFiles": [
                {
                  "path": "samples/ai-patch/buggy.aisharp",
                  "unifiedDiff": "__DIFF__"
                }
              ],
              "rationale": "Return a 10 percent discount for VIP customers.",
              "testsToRun": [
                "dotnet run --project tests/AISharp.Tests/AISharp.Tests.csproj"
              ],
              "riskLevel": "Low",
              "policyAcknowledgements": [
                "No secrets included in the diff.",
                "No network calls added."
              ]
            }
            """.Replace("__DIFF__", JsonEncodedText.Encode(unifiedDiff).ToString(), StringComparison.Ordinal);

        var runtime = new FakeLocalModelRuntime();
        runtime.Register("local", "patch-proposer", "propose-patch",
            _ => new ModelResponse(true, proposalJson, null, null, null));

        var registry = new SchemaRegistry();
        var provenanceDir = Path.Combine(outRoot, ".aisharp", "provenance");
        Directory.CreateDirectory(provenanceDir);
        using var store = new FileProvenanceStore(provenanceDir);
        provenanceStorePath = store.StorePath;

        var request = new PatchProposalRequest(
            TaskDescription: "Fix discountFor to honour VIP customers.",
            ContextPayload: "{\"diagnostic\":\"AI4001\",\"function\":\"discountFor\"}",
            PolicyName: "LocalOnlyDefault",
            SessionId: "ai-patch-sample");

        proposalResult = PatchProposalHelper
            .RequestAsync(request, runtime, registry, store)
            .GetAwaiter().GetResult();

        // Persist the raw proposal as an artifact (not provenance — the
        // provenance record intentionally omits raw diff content).
        File.WriteAllText(proposalArtifactPath, proposalJson);

        // 3. Optional --apply step. Writes the patched file to a COPY under
        //    the artifacts directory. The original sample file is never
        //    overwritten; we re-hash it below to prove non-mutation.
        string? appliedPath = null;
        if (apply && proposalResult.Success)
        {
            var appliedDir = Path.Combine(outRoot, "applied");
            Directory.CreateDirectory(appliedDir);
            appliedPath = Path.Combine(appliedDir, "buggy.aisharp");
            File.WriteAllText(appliedPath, fixedSource);
        }

        var sourceHashAfter = ComputeFileHash(samplePath);
        var sourceMutated = !string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal);

        // 4. Build the result payload.
        var artifacts = new List<ArtifactInfo>
        {
            new("source", Path.GetFullPath(samplePath)),
            new("patch-proposal", Path.GetFullPath(proposalArtifactPath)),
            new("provenance", Path.GetFullPath(provenanceStorePath))
        };
        if (appliedPath is not null)
            artifacts.Add(new ArtifactInfo("applied-source", Path.GetFullPath(appliedPath)));

        var diagnostics = new List<object>();
        if (!proposalResult.Success)
        {
            diagnostics.Add(new
            {
                code = "AI4002",
                severity = "error",
                message = proposalResult.ErrorMessage ?? "Patch proposal workflow failed.",
                location = (object?)null
            });
        }

        var aiPatchPayload = new
        {
            sample = "ai-patch",
            diagnostic = aiDiagnostic,
            offline = true,
            providerKind = "Local",
            proposal = proposalResult.Proposal is null
                ? null
                : new
                {
                    rationale = proposalResult.Proposal.Rationale,
                    riskLevel = proposalResult.Proposal.RiskLevel.ToString(),
                    changedFiles = proposalResult.Proposal.ChangedFiles
                        .Select(f => f.Path).ToArray(),
                    testsToRun = proposalResult.Proposal.TestsToRun,
                    policyAcknowledgements = proposalResult.Proposal.PolicyAcknowledgements,
                    proposalHash = proposalResult.Proposal.ProposalHash,
                    changedFileCount = proposalResult.Proposal.ChangedFiles.Count
                },
            schemaValidation = new
            {
                schemaName = PatchProposalSchema.SchemaName,
                isValid = proposalResult.Success,
                errors = proposalResult.Success
                    ? Array.Empty<string>()
                    : new[] { proposalResult.ErrorMessage ?? "validation failed" }
            },
            provenance = proposalResult.Provenance is null
                ? null
                : new
                {
                    kind = nameof(ProvenanceRecordKind.PatchProposal),
                    timestamp = proposalResult.Provenance.Timestamp,
                    proposalHash = proposalResult.Provenance.ProposalHash,
                    changedFileCount = proposalResult.Provenance.ChangedFileCount,
                    riskLevel = proposalResult.Provenance.RiskLevel,
                    wasApplied = proposalResult.Provenance.WasApplied,
                    sessionId = proposalResult.Provenance.SessionId,
                    storePath = Path.GetFullPath(provenanceStorePath)
                },
            applied = appliedPath is not null,
            appliedPath = appliedPath is null ? null : Path.GetFullPath(appliedPath),
            sourceMutated,
            sourceHashBefore,
            sourceHashAfter,
            compilerDiagnostics = ToJsonDiagnostics(compilation)
        };

        var extra = new Dictionary<string, object?>
        {
            ["aiPatch"] = aiPatchPayload,
            ["document"] = BuildDocumentPayload(compilation, compilation.SourcePath)
        };

        var success = proposalResult.Success && !sourceMutated;
        var exitCode = success ? ExitSuccess : ExitCommandFailure;

        if (sourceMutated)
        {
            diagnostics.Add(new
            {
                code = "AI4003",
                severity = "error",
                message = $"Source file was mutated unexpectedly: {samplePath}",
                location = (object?)null
            });
        }

        var payload = CreateResultPayload(
            Command,
            success,
            diagnostics,
            artifacts,
            stopwatch,
            extra);
        return new CommandResult(exitCode, payload);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or PolicyViolationException or SchemaNotFoundException
        or InvalidOperationException)
    {
        return Failure(Command,
            $"AI patch sample failed: {ex.Message}",
            ExitCommandFailure, stopwatch);
    }
}

static CommandResult RunFormatCommand(string[] args, Stopwatch stopwatch)
{
    if (!TryGetPositional(args, 1, out var path, out var error))
        return Failure("format", error, ExitCommandFailure, stopwatch);

    if (!File.Exists(path))
        return Failure("format", $"Source file not found: {path}", ExitCommandFailure, stopwatch);

    try
    {
        var source = File.ReadAllText(path);
        var formatted = AISharpFormatter.Format(source);
        var changed = !string.Equals(
            source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal),
            formatted,
            StringComparison.Ordinal);
        var checkOnly = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var printOnly = args.Contains("--stdout", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--print", StringComparer.OrdinalIgnoreCase);

        if (checkOnly && changed)
        {
            var payload = CreateResultPayload(
                "format",
                success: false,
                diagnostics:
                [
                    new
                    {
                        code = "AI3001",
                        severity = "error",
                        message = $"Source file is not formatted: {path}",
                        location = (object?)null
                    }
                ],
                artifacts: [new ArtifactInfo("source", Path.GetFullPath(path))],
                stopwatch,
                extra: new Dictionary<string, object?> { ["changed"] = true });
            return new CommandResult(ExitCommandFailure, payload);
        }

        if (printOnly)
        {
            Console.Write(formatted);
        }
        else if (!checkOnly)
        {
            File.WriteAllText(path, formatted);
        }

        var compilation = Compile(path);
        var exitCode = ExitCodeForCompilation(compilation);
        var artifactKind = checkOnly ? "source" : printOnly ? "formatted-output" : "formatted-source";
        return FromCompilation(
            "format",
            compilation,
            [new ArtifactInfo(artifactKind, Path.GetFullPath(path))],
            exitCode,
            stopwatch,
            new Dictionary<string, object?> { ["changed"] = changed });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Failure("format", $"Format failed: {ex.Message}", ExitCommandFailure, stopwatch);
    }
}

static CommandResult RunAuditCommand(string[] args, Stopwatch stopwatch)
{
    if (!TryGetPositional(args, 1, out var path, out var error))
        return Failure("audit", error, ExitCommandFailure, stopwatch);

    if (!File.Exists(path))
        return Failure("audit", $"Spec file not found: {path}", ExitCommandFailure, stopwatch);

    try
    {
        var audit = SpecProgressAuditor.Audit(File.ReadAllText(path));
        var payload = CreateResultPayload(
            "audit",
            success: true,
            diagnostics: [],
            artifacts: [new ArtifactInfo("spec", Path.GetFullPath(path))],
            stopwatch,
            extra: new Dictionary<string, object?>
            {
                ["audit"] = audit
            });
        return new CommandResult(ExitSuccess, payload);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Failure("audit", $"Audit failed: {ex.Message}", ExitCommandFailure, stopwatch);
    }
}

static CommandResult RunReleaseCommand(string[] args, Stopwatch stopwatch)
{
    var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    var repoRoot = FindRepositoryRoot();
    var releaseDir = Path.Combine(repoRoot, "artifacts", "release",
        $"release-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
    Directory.CreateDirectory(releaseDir);

    var gates = new List<ReleaseGateResult>();
    var packagesDir = Path.Combine(releaseDir, "packages");
    Directory.CreateDirectory(packagesDir);

    // Gate 1: Build (Release)
    var buildResult = RunDotnetProcess("dotnet",
        $"build {Quote(Path.Combine(repoRoot, "AISharp.sln"))} -c Release --nologo", repoRoot);
    gates.Add(new ReleaseGateResult("build", buildResult.ExitCode == 0,
        buildResult.ExitCode == 0 ? null : FirstNonEmptyLine(buildResult.Stderr, buildResult.Stdout)));

    // Gate 2: Tests
    var testResult = RunDotnetProcess("dotnet",
        $"run --project {Quote(Path.Combine(repoRoot, "tests", "AISharp.Tests", "AISharp.Tests.csproj"))} -c Release --no-build",
        repoRoot,
        new Dictionary<string, string> { [SkipCliGateTestsVariable] = "1" });
    var testPassed = testResult.ExitCode == 0;
    var testDetail = testPassed
        ? $"{testResult.Stdout.Split('\n').Count(l => l.StartsWith("PASS", StringComparison.Ordinal))} tests passed"
        : FirstNonEmptyLine(testResult.Stderr, testResult.Stdout, "Tests failed.");
    gates.Add(new ReleaseGateResult("tests", testPassed, testDetail));

    // Gate 3: Pack
    var packResult = PackLibraryProjects(repoRoot, packagesDir);
    gates.Add(new ReleaseGateResult("pack", packResult.ExitCode == 0, packResult.Detail));

    // Collect packages
    var packagePaths = Directory.GetFiles(packagesDir, "*.nupkg")
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();

    // Gate 4: Hash manifest
    string? hashManifestPath = null;
    try
    {
        if (packagePaths.Count > 0)
        {
            var hashManifest = ReleaseHashManifest.Create("aisharp", "0.1.0-preview", packagesDir, packagePaths);
            hashManifestPath = Path.Combine(releaseDir, "release-hashes.json");
            File.WriteAllText(hashManifestPath, hashManifest.Serialize());
            gates.Add(new ReleaseGateResult("hash-manifest", true, $"{packagePaths.Count} artifacts hashed."));
        }
        else
        {
            gates.Add(new ReleaseGateResult("hash-manifest", false, "No .nupkg files found to hash."));
        }
    }
    catch (Exception ex)
    {
        gates.Add(new ReleaseGateResult("hash-manifest", false, ex.Message));
    }

    // Gate 5: SBOM
    string? sbomPath = null;
    try
    {
        var sbom = Releasesbom.CreateMinimal("aisharp", "0.1.0-preview");
        sbomPath = Path.Combine(releaseDir, "sbom.json");
        File.WriteAllText(sbomPath, sbom.Serialize());
        gates.Add(new ReleaseGateResult("sbom", true, $"{sbom.Components.Count} components in SBOM."));
    }
    catch (Exception ex)
    {
        gates.Add(new ReleaseGateResult("sbom", false, ex.Message));
    }

    // Gate 6: Signing dry-run
    var signingConfigPath = GetOptionValue(args, "--signing-config");
    var (signingConfig, signingConfigDiag) = LoadSigningConfigurationOrDryRun(signingConfigPath);
    var signingResult = ReleaseSigningService.DryRun(signingConfig, packagePaths);
    var signingGatePassed = (signingResult.Success || dryRun) && signingConfigDiag is null;
    gates.Add(new ReleaseGateResult("signing", signingGatePassed,
        signingConfigDiag ?? (dryRun ? "Dry-run: signing skipped." : null)));

    var signaturePath = signingResult.SignatureArtifactPath;

    // Write provenance
    var sourceCommit = GetGitCommit(repoRoot);
    var provenance = new ReleaseProvenanceRecord(
        PackageName: "aisharp",
        PackageVersion: "0.1.0-preview",
        SourceCommit: sourceCommit,
        BuildCommand: "dotnet pack <packable-project> -c Release --no-build",
        GeneratedAt: DateTimeOffset.UtcNow,
        PackagePaths: packagePaths,
        HashManifestPath: hashManifestPath,
        SbomPath: sbomPath,
        SignaturePath: signaturePath,
        IsDryRun: dryRun,
        Gates: gates);

    var provenancePath = Path.Combine(releaseDir, "provenance.json");
    File.WriteAllText(provenancePath, provenance.Serialize());

    var allPassed = gates.All(g => g.Passed);

    var payload = CreateResultPayload(
        "release",
        allPassed,
        diagnostics: allPassed ? [] : new object[]
        {
            new { code = "AI5001", severity = "error", message = "One or more release gates failed.", location = (object?)null }
        },
        artifacts: packagePaths.Select(p => new ArtifactInfo("package", p))
            .Concat(hashManifestPath is not null ? [new ArtifactInfo("hash-manifest", hashManifestPath)] : [])
            .Concat(sbomPath is not null ? [new ArtifactInfo("sbom", sbomPath)] : [])
            .Append(new ArtifactInfo("provenance", provenancePath)),
        stopwatch,
        extra: new Dictionary<string, object?>
        {
            ["release"] = new
            {
                isDryRun = dryRun,
                releaseDirectory = releaseDir,
                packagesDirectory = packagesDir,
                hashManifestPath,
                sbomPath,
                provenancePath,
                sourceCommit,
                signingState = ResolveSigningState(dryRun, signingResult, signingConfig),
                signaturePath,
                gates = gates.Select(g => new { g.Name, g.Passed, g.Detail })
            }
        });

    return new CommandResult(allPassed ? ExitSuccess : ExitLoweringOrBuildFailure, payload);
}

static CommandResult RunSignCommand(string[] args, Stopwatch stopwatch)
{
    var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    var configPath = GetOptionValue(args, "--config");
    var signatureOut = GetOptionValue(args, "--signature-out");
    var artifactDir = GetOptionValue(args, "--artifact-dir");

    if (!dryRun)
    {
        return Failure("sign",
            "Real signing is not yet implemented. Re-run with --dry-run to validate the artifact list and configuration.",
            ExitCommandFailure, stopwatch);
    }

    SigningConfiguration config;
    string? configDiag = null;
    try
    {
        (config, configDiag) = LoadSigningConfigurationOrDryRun(configPath);
    }
    catch (InvalidOperationException ex)
    {
        return FailWithSigningDiagnostic("sign",
            ReleaseSigningService.DiagMissingConfig, ex.Message, stopwatch);
    }

    var artifactPaths = CollectSignArtifactPaths(args, artifactDir);

    var signatureOutputPath = signatureOut ?? config.SignatureOutputPath;
    var result = ReleaseSigningService.DryRun(config, artifactPaths, signatureOutputPath);

    var diagnostics = new List<object>();
    if (configDiag is not null)
    {
        diagnostics.Add(new
        {
            code = ReleaseSigningService.DiagMissingConfig,
            severity = "error",
            message = configDiag,
            location = (object?)null
        });
    }

    foreach (var diag in result.Diagnostics)
    {
        diagnostics.Add(new
        {
            code = diag.Code,
            severity = diag.Severity.ToString().ToLowerInvariant(),
            message = diag.Message,
            location = (object?)null
        });
    }

    var success = result.Success && configDiag is null;

    var payload = CreateResultPayload(
        "sign",
        success,
        diagnostics: diagnostics,
        artifacts: result.ArtifactPaths.Select(p => new ArtifactInfo("artifact", p)),
        stopwatch,
        extra: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sign"] = new
            {
                isDryRun = result.IsDryRun,
                provider = result.Provider.ToString().ToLowerInvariant(),
                providerHint = config.ExternalProviderHint,
                configurationPath = configPath,
                signatureArtifactPath = result.SignatureArtifactPath,
                artifactCount = result.ArtifactPaths.Count,
                signingState = ResolveSigningState(dryRunFlag: true, result, config)
            }
        });

    var exitCode = success ? ExitSuccess : (configDiag is not null ? ExitCommandFailure : ExitDiagnostics);
    return new CommandResult(exitCode, payload);
}

static CommandResult FailWithSigningDiagnostic(string command, string code, string message, Stopwatch stopwatch)
{
    var payload = CreateResultPayload(
        command,
        success: false,
        diagnostics: new[]
        {
            new { code, severity = "error", message, location = (object?)null }
        },
        artifacts: Array.Empty<ArtifactInfo>(),
        stopwatch,
        extra: null);
    return new CommandResult(ExitCommandFailure, payload);
}

static (SigningConfiguration Config, string? Diagnostic) LoadSigningConfigurationOrDryRun(string? configPath)
{
    if (string.IsNullOrWhiteSpace(configPath))
    {
        return (SigningConfiguration.DryRunOnly, null);
    }

    if (!File.Exists(configPath))
    {
        return (SigningConfiguration.DryRunOnly,
            ReleaseSigningService.FormatMissingConfigurationDiagnostic(configPath));
    }

    var loaded = SigningConfiguration.LoadFromFile(configPath)
        ?? SigningConfiguration.DryRunOnly;
    return (loaded, null);
}

static IReadOnlyList<string> CollectSignArtifactPaths(string[] args, string? artifactDir)
{
    var paths = new List<string>();

    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--artifact", StringComparison.OrdinalIgnoreCase))
        {
            paths.Add(args[i + 1]);
        }
    }

    if (!string.IsNullOrWhiteSpace(artifactDir) && Directory.Exists(artifactDir))
    {
        foreach (var file in Directory.GetFiles(artifactDir, "*.nupkg").OrderBy(p => p, StringComparer.Ordinal))
        {
            paths.Add(file);
        }
    }

    return paths;
}

static string ResolveSigningState(bool dryRunFlag, SigningResult signingResult, SigningConfiguration config)
{
    if (!dryRunFlag && !signingResult.IsDryRun)
    {
        return signingResult.SignatureArtifactPath is not null ? "signed" : "unsigned";
    }

    return config.Provider == SigningProviderKind.None ? "dry-run-no-provider" : "dry-run";
}

static CommandResult RunAcceptanceCommand(string[] args, Stopwatch stopwatch)
{
    var repoRoot = FindRepositoryRoot();
    var logRoot = Path.Combine(repoRoot, "artifacts", "acceptance");
    try
    {
        if (Directory.Exists(logRoot))
            Directory.Delete(logRoot, recursive: true);
        Directory.CreateDirectory(logRoot);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Failure("acceptance",
            $"Failed to prepare acceptance log directory '{logRoot}': {ex.Message}",
            ExitCommandFailure, stopwatch);
    }

    var cliProject = Path.Combine(repoRoot, "src", "AISharp.Cli", "AISharp.Cli.csproj");
    var testsProject = Path.Combine(repoRoot, "tests", "AISharp.Tests", "AISharp.Tests.csproj");
    var solutionPath = Path.Combine(repoRoot, "AISharp.sln");
    var ordersSample = Path.Combine(repoRoot, "samples", "orders.aisharp");
    var serviceLibraryDir = Path.Combine(repoRoot, "samples", "service-library");
    var actorSample = Path.Combine(repoRoot, "samples", "actor-workflow", "worker.aisharp");
    var lowerOutDir = Path.Combine(repoRoot, "artifacts", "acceptance-lower");
    Directory.CreateDirectory(lowerOutDir);

    var gates = new List<AcceptanceGateResult>(7);

    // Gate 1: Build the whole solution.
    gates.Add(RunAcceptanceGate("build", logRoot, "dotnet",
        $"build {Quote(solutionPath)} --nologo", repoRoot,
        detailOnSuccess: "solution build succeeded"));

    // Gate 2: Run the executable test runner. Set the skip flag so the CLI
    // gate self-tests (which themselves invoke acceptance/release) don't
    // recurse and double-build inside the gate.
    gates.Add(RunAcceptanceGate("tests", logRoot, "dotnet",
        $"run --project {Quote(testsProject)} --no-build",
        repoRoot,
        env: new Dictionary<string, string> { [SkipCliGateTestsVariable] = "1" },
        detailFromOutput: (stdout, stderr) =>
        {
            var combined = stdout + "\n" + stderr;
            var passCount = combined.Split('\n').Count(l => l.StartsWith("PASS", StringComparison.Ordinal));
            var failCount = combined.Split('\n').Count(l => l.StartsWith("FAIL", StringComparison.Ordinal));
            var skipCount = combined.Split('\n').Count(l => l.StartsWith("SKIP", StringComparison.Ordinal));
            return $"{passCount} passed, {failCount} failed, {skipCount} skipped";
        }));

    // Gate 3: CLI validation of the representative sample.
    gates.Add(RunAcceptanceGate("cli-sample-validate", logRoot, "dotnet",
        $"run --project {Quote(cliProject)} --no-build -- validate {Quote(ordersSample)} --json",
        repoRoot,
        detailOnSuccess: "samples/orders.aisharp validated"));

    // Gate 4: Lower + build the multi-file service library sample. The CLI
    // `build` command compiles each `.aisharp` source, generates C#, and
    // invokes `dotnet build` on the generated csproj, fulfilling the
    // "lower/build service sample" requirement.
    gates.Add(RunAcceptanceGate("service-library-build", logRoot, "dotnet",
        $"run --project {Quote(cliProject)} --no-build -- build {Quote(serviceLibraryDir)} --json",
        repoRoot,
        detailOnSuccess: "samples/service-library lowered and built"));

    // Gate 5: AI patch fake-provider sample (offline, deterministic).
    gates.Add(RunAcceptanceGate("ai-patch-sample", logRoot, "dotnet",
        $"run --project {Quote(cliProject)} --no-build -- run-sample ai-patch --json",
        repoRoot,
        detailOnSuccess: "AI patch fake-provider sample succeeded"));

    // Gate 6: Actor / async workflow sample validation.
    if (File.Exists(actorSample))
    {
        gates.Add(RunAcceptanceGate("actor-sample-validate", logRoot, "dotnet",
            $"run --project {Quote(cliProject)} --no-build -- validate {Quote(actorSample)} --json",
            repoRoot,
            detailOnSuccess: "samples/actor-workflow validated"));
    }
    else
    {
        var actorLog = Path.Combine(logRoot, "actor-sample-validate.log");
        var missingMessage = $"Actor sample not found at '{actorSample}'. " +
            "Add the sample (PR-39) to satisfy this gate.";
        File.WriteAllText(actorLog, missingMessage + Environment.NewLine);
        gates.Add(new AcceptanceGateResult(
            "actor-sample-validate",
            Passed: false,
            Detail: $"actor sample missing; see log: {actorLog}",
            LogPath: actorLog));
    }

    // Gate 7: Release dry-run (exercises pack, hash manifest, SBOM,
    // provenance, signing dry-run gates).
    gates.Add(RunAcceptanceGate("release-dry-run", logRoot, "dotnet",
        $"run --project {Quote(cliProject)} --no-build -- release --dry-run --json",
        repoRoot,
        detailOnSuccess: "release dry-run gates passed"));

    var allPassed = gates.All(g => g.Passed);
    var summaryPath = Path.Combine(logRoot, "summary.json");

    var artifacts = new List<ArtifactInfo>
    {
        new("acceptance-log-dir", Path.GetFullPath(logRoot)),
        new("acceptance-summary", Path.GetFullPath(summaryPath))
    };
    foreach (var gate in gates)
    {
        if (!string.IsNullOrEmpty(gate.LogPath))
            artifacts.Add(new ArtifactInfo($"log-{gate.Name}", Path.GetFullPath(gate.LogPath!)));
    }

    var payload = CreateResultPayload(
        "acceptance",
        allPassed,
        diagnostics: gates
            .Where(g => !g.Passed)
            .Select(g => (object)new
            {
                code = "AI5010",
                severity = "error",
                message = $"Acceptance gate '{g.Name}' failed: {g.Detail ?? "see log"}",
                location = (object?)null
            })
            .ToArray(),
        artifacts,
        stopwatch,
        extra: new Dictionary<string, object?>
        {
            ["acceptance"] = new
            {
                passed = gates.Count(g => g.Passed),
                failed = gates.Count(g => !g.Passed),
                total = gates.Count,
                logDirectory = Path.GetFullPath(logRoot),
                summaryPath = Path.GetFullPath(summaryPath),
                gates = gates.Select(g => new
                {
                    name = g.Name,
                    passed = g.Passed,
                    detail = g.Detail,
                    logPath = g.LogPath is null ? null : Path.GetFullPath(g.LogPath)
                })
            }
        });

    try
    {
        File.WriteAllText(summaryPath, SerializeJson(payload));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Surface but don't fail acceptance solely on summary write issues —
        // the gate results themselves are still in the returned payload.
        Console.Error.WriteLine($"Warning: failed to write acceptance summary '{summaryPath}': {ex.Message}");
    }

    return new CommandResult(allPassed ? ExitSuccess : ExitLoweringOrBuildFailure, payload);
}

static AcceptanceGateResult RunAcceptanceGate(
    string name,
    string logRoot,
    string fileName,
    string arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? env = null,
    string? detailOnSuccess = null,
    Func<string, string, string>? detailFromOutput = null)
{
    var logPath = Path.Combine(logRoot, $"{name}.log");
    (int ExitCode, string Stdout, string Stderr) result;
    try
    {
        result = RunDotnetProcess(fileName, arguments, workingDirectory, env);
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        var failureLog = $"Failed to start acceptance gate '{name}': {ex.Message}";
        try { File.WriteAllText(logPath, failureLog + Environment.NewLine); }
        catch (Exception) { /* best-effort */ }
        return new AcceptanceGateResult(name, false,
            $"failed to start: {ex.Message}; see log: {logPath}", logPath);
    }

    try
    {
        var log = new StringBuilder();
        log.Append("$ ").Append(fileName).Append(' ').AppendLine(arguments);
        log.Append("# working-directory: ").AppendLine(workingDirectory);
        log.Append("# exit-code: ").AppendLine(result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (env is not null && env.Count > 0)
        {
            log.AppendLine("# environment:");
            foreach (var kvp in env)
                log.Append("#   ").Append(kvp.Key).Append('=').AppendLine(kvp.Value);
        }
        log.AppendLine("--- stdout ---");
        log.AppendLine(result.Stdout);
        log.AppendLine("--- stderr ---");
        log.AppendLine(result.Stderr);
        File.WriteAllText(logPath, log.ToString());
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Warning: failed to write acceptance log '{logPath}': {ex.Message}");
    }

    var passed = result.ExitCode == 0;
    string? detail;
    if (passed)
    {
        detail = detailFromOutput is not null
            ? detailFromOutput(result.Stdout, result.Stderr)
            : detailOnSuccess;
    }
    else
    {
        var firstLine = FirstNonEmptyLine(result.Stderr, result.Stdout, "see log");
        var summary = detailFromOutput is not null
            ? detailFromOutput(result.Stdout, result.Stderr)
            : firstLine;
        detail = $"exit {result.ExitCode}: {summary}; see log: {logPath}";
    }

    return new AcceptanceGateResult(name, passed, detail, logPath);
}

static (int ExitCode, string Stdout, string Stderr) RunDotnetProcess(
    string fileName,
    string arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment = null,
    int timeoutMilliseconds = ChildProcessTimeoutMilliseconds)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };

    if (environment is not null)
    {
        foreach (var item in environment)
            startInfo.Environment[item.Key] = item.Value;
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start {fileName}.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(timeoutMilliseconds))
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        var timeoutStdout = stdoutTask.GetAwaiter().GetResult();
        var timeoutStderr = stderrTask.GetAwaiter().GetResult();
        return (-1, timeoutStdout, $"{timeoutStderr}{Environment.NewLine}Timed out after {timeoutMilliseconds / 1000} seconds.");
    }

    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();
    return (process.ExitCode, stdout, stderr);
}

static (int ExitCode, string Detail) PackLibraryProjects(string repoRoot, string packagesDir)
{
    var packed = 0;
    foreach (var projectRelativePath in PackableProjectRelativePaths())
    {
        var projectPath = Path.Combine(repoRoot, projectRelativePath);
        var result = RunDotnetProcess(
            "dotnet",
            $"pack {Quote(projectPath)} -c Release --no-build --output {Quote(packagesDir)} --nologo",
            repoRoot);

        if (result.ExitCode != 0)
        {
            var detail = FirstNonEmptyLine(result.Stderr, result.Stdout, "dotnet pack failed.");
            return (result.ExitCode, $"dotnet pack {projectRelativePath} failed: {detail}");
        }

        packed++;
    }

    return (ExitSuccess, $"{packed} packable projects packed.");
}

static IReadOnlyList<string> PackableProjectRelativePaths() =>
[
    Path.Combine("src", "AISharp.Compiler", "AISharp.Compiler.csproj"),
    Path.Combine("src", "AISharp.Lowering", "AISharp.Lowering.csproj"),
    Path.Combine("src", "AISharp.Runtime", "AISharp.Runtime.csproj"),
    Path.Combine("src", "AISharp.LanguageServer", "AISharp.LanguageServer.csproj"),
    Path.Combine("src", "AISharp.Package", "AISharp.Package.csproj"),
    Path.Combine("src", "AISharp.Cli", "AISharp.Cli.csproj"),
];

static string? GetGitCommit(string workingDirectory)
{
    try
    {
        var (exitCode, stdout, _) = RunDotnetProcess("git", "rev-parse --short HEAD", workingDirectory);
        return exitCode == 0 ? stdout.Trim() : null;
    }
    catch
    {
        return null;
    }
}

static CommandResult RunDotnetBuildProject(
    string command,
    string projectPath,
    Stopwatch stopwatch,
    IReadOnlyList<ArtifactInfo>? existingArtifacts = null)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"build {Quote(projectPath)}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    });

    if (process is null)
        return Failure(command, "Failed to start dotnet build.", ExitLoweringOrBuildFailure, stopwatch);

    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    var artifacts = new List<ArtifactInfo>();
    if (existingArtifacts is not null)
        artifacts.AddRange(existingArtifacts);
    artifacts.Add(new ArtifactInfo("project", Path.GetFullPath(projectPath)));

    var targetFramework = "net9.0";
    var assemblyPath = Path.Combine(
        Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory(),
        "bin",
        "Debug",
        targetFramework,
        $"{Path.GetFileNameWithoutExtension(projectPath)}.dll");
    if (File.Exists(assemblyPath))
        artifacts.Add(new ArtifactInfo("assembly", Path.GetFullPath(assemblyPath)));

    var diagnostics = process.ExitCode == 0
        ? Array.Empty<object>()
        : new object[]
        {
            new
            {
                code = "AI3001",
                severity = "error",
                message = FirstNonEmptyLine(stderr, stdout, "dotnet build failed."),
                location = (object?)null
            }
        };

    var payload = CreateResultPayload(
        command,
        process.ExitCode == 0,
        diagnostics,
        artifacts,
        stopwatch,
        new Dictionary<string, object?>
        {
            ["build"] = new
            {
                exitCode = process.ExitCode,
                stdout,
                stderr
            }
        });

    return new CommandResult(process.ExitCode == 0 ? ExitSuccess : ExitLoweringOrBuildFailure, payload);
}

static int RunLegacyValidate(string[] args, bool json)
{
    var path = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
    if (path is null)
    {
        Console.Error.WriteLine("No source file was provided.");
        return ExitCommandFailure;
    }

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Source file not found: {path}");
        return ExitCommandFailure;
    }

    try
    {
        var compilation = Compile(path);
        var hasBlockingSemanticErrors = compilation.SemanticResult.SemanticDiagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.Code is "AI3030" or "AI3031" or "AI3040" or "AI3041");
        var cliSuccess = compilation.ParseResult.Success && !hasBlockingSemanticErrors;

        if (json)
        {
            Console.WriteLine(SerializeJson(BuildDocumentPayload(compilation, path)));
        }
        else
        {
            WriteLegacyHuman(compilation, cliSuccess);
        }

        return cliSuccess ? ExitSuccess : ExitDiagnostics;
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"Cannot read source file because access was denied: {ex.Message}");
        return ExitCommandFailure;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"Cannot read source file: {ex.Message}");
        return ExitCommandFailure;
    }
}

static CompilationResult Compile(string path)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"Source file not found: {path}", path);

    var source = File.ReadAllText(path);
    var profileArguments = NormalizeLanguageProfileArguments(Environment.GetCommandLineArgs());
    var profile = profileArguments.FirstOrDefault(a => a.StartsWith("--profile=", StringComparison.Ordinal))?["--profile=".Length..]
        ?? ScalarLanguage.LegacyProfile;
    var parseResult = AISharpParser.Parse(source, profile);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult = AISharpHirBuilder.Build(semanticResult);
    var mirResult = AISharpMirBuilder.Build(hirResult);
    return new CompilationResult(path, parseResult, semanticResult, hirResult, mirResult);
}

static int ExitCodeForCompilation(CompilationResult compilation)
{
    var diagnostics = AllDiagnostics(compilation).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (diagnostics.Length == 0)
        return ExitSuccess;

    return diagnostics.Any(IsPolicyOrSecurityDiagnostic) ? ExitPolicyOrSecurityFailure : ExitDiagnostics;
}

static bool IsPolicyOrSecurityDiagnostic(Diagnostic diagnostic) =>
    diagnostic.Code is "AI2013" or "AI2015" or "AI2017" or "AI2020" or "AI2021" or "AI2022" or "AI2023"
        or "AI2024" or "AI2025" or "AI2033" or "AI2034" or "AI2044" or "AI2049" or "AI2050" or "AI2051"
        or "AI2052" or "AI2053";

static IEnumerable<Diagnostic> AllDiagnostics(CompilationResult compilation) =>
    compilation.ParseResult.Diagnostics.Concat(compilation.SemanticResult.SemanticDiagnostics);

static object BuildDocumentPayload(CompilationResult compilation, string path)
{
    var result = compilation.ParseResult;
    var semanticResult = compilation.SemanticResult;
    return new
    {
        success = semanticResult.Success,
        profile = result.Document.Profile,
        scalarFunctions = result.Document.ScalarFunctions,
        module = result.Document.Module,
        intent = result.Document.Intent,
        uses = result.Document.Uses.Select(u => u.Path),
        policies = result.Document.Policies.Select(p => new
        {
            name = p.Name,
            classification = p.Classification,
            inference = p.Inference,
            training = p.Training,
            telemetry = p.Telemetry,
            externalText = p.ExternalText,
            capabilities = p.Capabilities,
            fields = p.Fields.Select(f => new { key = f.Key, value = f.Value }),
            location = ToJsonLocation(p.Location, path)
        }),
        capabilities = result.Document.Capabilities.Select(c => c.Name),
        resources = result.Document.Resources.Select(r => r.Name),
        effects = result.Document.Effects,
        types = result.Document.Types.Select(t => new
        {
            t.Name,
            Kind = t.Kind,
            t.Cases,
            Fields = t.Fields,
            location = ToJsonLocation(t.Location, path)
        }),
        schemas = result.Document.Schemas.Select(s => new
        {
            name = s.Name,
            fields = s.Fields,
            metadata = ToJsonSchemaMetadata(result.Document, s.Name, path),
            location = ToJsonLocation(s.Location, path)
        }),
        tools = result.Document.Tools.Select(t => new
        {
            t.Name,
            parameters = t.Parameters,
            t.ReturnType,
            t.Sandbox,
            t.Capabilities,
            t.Timeout,
            location = ToJsonLocation(t.Location, path)
        }),
        macros = result.Document.Macros.Select(m => new
        {
            m.Name,
            parameters = m.Parameters,
            m.ReturnType,
            m.Input,
            m.Expands,
            body = new
            {
                m.Body.Text,
                location = ToJsonLocation(m.Body.Location, path)
            },
            location = ToJsonLocation(m.Location, path)
        }),
        generators = result.Document.Generators.Select(g => new
        {
            g.Name,
            parameters = g.Parameters,
            g.ReturnType,
            g.Incremental,
            g.Deterministic,
            g.Tracks,
            g.Output,
            body = new
            {
                g.Body.Text,
                location = ToJsonLocation(g.Body.Location, path)
            },
            location = ToJsonLocation(g.Location, path)
        }),
        functions = result.Document.Functions.Select(function => ToJsonFunctionDeclaration(function, path)),
        actors = result.Document.Actors.Select(a => new
        {
            a.Name,
            a.RuntimeProfile,
            stateMembers = a.StateMembers.Select(state => new
            {
                state.Name,
                state.Type,
                location = ToJsonLocation(state.Location, path)
            }),
            handlers = a.Handlers.Select(handler => ToJsonFunctionDeclaration(handler, path)),
            location = ToJsonLocation(a.Location, path)
        }),
        tests = result.Document.Tests.Select(t => t.Name),
        observability = result.Document.Observability.Select(o => new { o.Kind, o.Name }),
        symbols = semanticResult.SymbolTable.AllDocumentSymbols.Select(s => ToJsonSymbol(s, path)),
        diagnostics = result.Diagnostics.Select(d => ToJsonDiagnostic(d, path)),
        semanticDiagnostics = semanticResult.SemanticDiagnostics.Select(d => ToJsonDiagnostic(d, path)),
        resolvedTypeReferences = semanticResult.ResolvedTypeReferences.Select(r => new
        {
            r.TypeText,
            r.Context,
            location = ToJsonLocation(r.Location, path),
            resolvedName = r.ResolvedSymbol?.Name,
            resolvedKind = r.ResolvedSymbol?.Kind.ToString()
        }),
        resolvedSymbolReferences = semanticResult.ResolvedSymbolReferences.Select(r => new
        {
            r.Name,
            referenceKind = r.ReferenceKind.ToString(),
            r.Context,
            location = ToJsonLocation(r.Location, path),
            resolvedName = r.ResolvedSymbol?.Name,
            resolvedKind = r.ResolvedSymbol?.Kind.ToString(),
            resolvedLocation = r.ResolvedSymbol is null ? null : ToJsonLocation(r.ResolvedSymbol.Location, path)
        }),
        effectSummary = semanticResult.EffectCheckResult?.FunctionEffects.Select(fe => new
        {
            fe.FunctionName,
            fe.DeclaredEffects,
            fe.InferredRequiredEffects,
            fe.IsCompliant
        }) ?? [],
        contractSummary = semanticResult.ContractCheckResult?.ContractInfos.Select(ci => new
        {
            ci.FunctionName,
            kind = ci.Kind.ToString().ToLowerInvariant(),
            ci.Expression,
            location = ToJsonLocation(ci.Location, path),
            ci.IsBoolean,
            ci.AvailableNames,
            ci.UnknownNames,
            ci.VerificationHook,
            checkPhase = ci.CheckPhase.ToString().ToLowerInvariant(),
            ci.EmitRuntimeCheck,
            ci.EmitVerificationCondition,
            runtimeCheck = ToJsonRuntimeContractCheck(ci.RuntimeCheck, path),
            verificationCondition = ToJsonVerificationCondition(ci.VerificationCondition, path)
        }) ?? []
    };
}

static object CreateResultPayload(
    string command,
    bool success,
    IEnumerable<object> diagnostics,
    IEnumerable<ArtifactInfo> artifacts,
    Stopwatch stopwatch,
    IReadOnlyDictionary<string, object?>? extra = null)
{
    stopwatch.Stop();
    var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["command"] = command,
        ["success"] = success,
        ["diagnostics"] = diagnostics,
        ["artifacts"] = artifacts,
        ["timing"] = new
        {
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
        }
    };

    if (extra is not null)
    {
        foreach (var item in extra)
            payload[item.Key] = item.Value;
    }

    return payload;
}

static object[] ToJsonDiagnostics(CompilationResult compilation) =>
    AllDiagnostics(compilation)
        .Select(d => ToJsonDiagnostic(d, compilation.SourcePath))
        .ToArray();

static object ToJsonDiagnostic(Diagnostic diagnostic, string sourcePath) => new
{
    diagnostic.Code,
    severity = diagnostic.Severity.ToString().ToLowerInvariant(),
    diagnostic.Message,
    location = ToJsonLocation(diagnostic.Location, sourcePath)
};

static object ToJsonSymbol(Symbol symbol, string sourcePath) => new
{
    symbol.Name,
    kind = symbol.Kind.ToString(),
    symbol.ActorScope,
    location = ToJsonLocation(symbol.Location, sourcePath)
};

static void WriteHumanResult(CommandResult result)
{
    var json = JsonSerializer.SerializeToElement(result.Payload, JsonOptions());
    Console.WriteLine(json.GetProperty("success").GetBoolean()
        ? $"AI# {json.GetProperty("command").GetString()} succeeded."
        : $"AI# {json.GetProperty("command").GetString()} failed.");

    foreach (var diagnostic in json.GetProperty("diagnostics").EnumerateArray())
        Console.Error.WriteLine($"{diagnostic.GetProperty("severity").GetString()} {diagnostic.GetProperty("code").GetString()}: {diagnostic.GetProperty("message").GetString()}");
}

static void WriteLegacyHuman(CompilationResult compilation, bool cliSuccess)
{
    var result = compilation.ParseResult;
    Console.WriteLine(cliSuccess ? "AI# validation succeeded." : "AI# validation failed.");
    Console.WriteLine($"Module: {result.Document.Module ?? "(none)"}");
    Console.WriteLine($"Intent: {result.Document.Intent ?? "(missing)"}");
    Console.WriteLine($"Uses: {result.Document.Uses.Count}");
    Console.WriteLine($"Policies: {result.Document.Policies.Count}");
    Console.WriteLine($"Capabilities: {result.Document.Capabilities.Count}");
    Console.WriteLine($"Resources: {result.Document.Resources.Count}");
    Console.WriteLine($"Effects: {result.Document.Effects.Count}");
    Console.WriteLine($"Types: {result.Document.Types.Count}");
    Console.WriteLine($"Schemas: {result.Document.Schemas.Count}");
    Console.WriteLine($"Tools: {result.Document.Tools.Count}");
    Console.WriteLine($"Functions: {result.Document.Functions.Count}");
    Console.WriteLine($"Tests: {result.Document.Tests.Count}");
    Console.WriteLine($"Observability: {result.Document.Observability.Count}");

    foreach (var diagnostic in result.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Location}: {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

    foreach (var diagnostic in compilation.SemanticResult.SemanticDiagnostics)
        Console.Error.WriteLine($"{diagnostic.Location}: {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
}

static void PrintHelp()
{
    Console.WriteLine("AI# compiler front end");
    Console.WriteLine("Usage:");
    Console.WriteLine("  aisharp <path-to-file.aisharp> [--json]");
    Console.WriteLine("  aisharp validate <file> [--json]");
    Console.WriteLine("  aisharp lower <file> --out <dir> [--json]");
    Console.WriteLine("  aisharp build <file-or-project> [--json]");
    Console.WriteLine("  aisharp build-native <file> --out <new.exe> --bits <32|64> --cc <absolute cl.exe> [--optimize] [--json]");
    Console.WriteLine("  aisharp run-sample <sample-name> [--json]");
    Console.WriteLine("  aisharp format <file> [--json]");
    Console.WriteLine("  aisharp audit <spec-file> [--json]");
    Console.WriteLine("  aisharp release --dry-run [--json]");
    Console.WriteLine("  aisharp sign --dry-run [--config <signing.json>] [--artifact <path>]... [--artifact-dir <dir>] [--signature-out <path>] [--json]");
    Console.WriteLine("  aisharp acceptance [--json]");
}

static bool TryGetPositional(string[] args, int index, out string value, out string error)
{
    var positionals = args
        .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
        .ToArray();

    if (positionals.Length <= index)
    {
        value = string.Empty;
        error = $"Missing required argument for '{args[0]}'.";
        return false;
    }

    value = positionals[index];
    error = string.Empty;
    return true;
}

static string? GetOptionValue(string[] args, string option)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

static string[] NormalizeLanguageProfileArguments(string[] arguments)
{
    var normalized = new List<string>();
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index] != "--profile") { normalized.Add(arguments[index]); continue; }
        var value = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? arguments[++index] : "";
        normalized.Add("--profile=" + value);
    }
    return normalized.ToArray();
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AISharp.sln")))
            return directory.FullName;

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static string SanitizeFileName(string value)
{
    var sanitized = Regex.Replace(value, @"[^A-Za-z0-9_.-]", "_");
    return string.IsNullOrWhiteSpace(sanitized) ? "aisharp" : sanitized;
}

static string FirstNonEmptyLine(params string[] values)
{
    foreach (var value in values)
    {
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line.Trim();
        }
    }

    return "Command failed.";
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

static string SerializeJson(object payload) => JsonSerializer.Serialize(payload, JsonOptions());

static JsonSerializerOptions JsonOptions()
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    return options;
}

static object ToJsonFunctionDeclaration(FunctionDeclaration function, string sourcePath) => new
{
    function.Name,
    function.IsAsync,
    parameters = function.Parameters,
    function.ReturnType,
    function.Effects,
    contracts = function.Contracts.Select(c => new
    {
        c.Kind,
        c.Expression,
        c.CheckPhase,
        c.EmitRuntimeCheck,
        c.EmitDebugCheck,
        c.EmitVerificationCondition,
        c.VerificationHook,
        location = ToJsonLocation(c.Location, sourcePath)
    }),
    body = new
    {
        statements = function.Body.Statements.Select(statement => ToJsonFunctionStatement(statement, sourcePath))
    },
    modelCalls = function.ModelCalls.Select(modelCall => ToJsonModelCall(modelCall, sourcePath)),
    location = ToJsonLocation(function.Location, sourcePath)
};

static object ToJsonRuntimeContractCheck(RuntimeContractCheckMetadata metadata, string sourcePath) => new
{
    metadata.Emit,
    phase = metadata.Phase.ToString().ToLowerInvariant(),
    metadata.FunctionName,
    kind = metadata.Kind.ToString().ToLowerInvariant(),
    metadata.Expression,
    location = ToJsonLocation(metadata.Location, sourcePath)
};

static object ToJsonVerificationCondition(VerificationConditionMetadata metadata, string sourcePath) => new
{
    metadata.Emit,
    metadata.Hook,
    metadata.FunctionName,
    kind = metadata.Kind.ToString().ToLowerInvariant(),
    metadata.Expression,
    location = ToJsonLocation(metadata.Location, sourcePath),
    metadata.AvailableNames
};

static object ToJsonFunctionStatement(FunctionStatement statement, string sourcePath) =>
    statement switch
    {
        LetStatement let => new
        {
            kind = "let",
            let.Name,
            let.TypeAnnotation,
            initializer = ToJsonExpression(let.Initializer, sourcePath),
            location = ToJsonLocation(let.Location, sourcePath)
        },
        ReturnStatement ret => new
        {
            kind = "return",
            expression = ToJsonExpression(ret.Expression, sourcePath),
            location = ToJsonLocation(ret.Location, sourcePath)
        },
        ExpressionStatement expr => new
        {
            kind = "expression",
            expression = ToJsonExpression(expr.Expression, sourcePath),
            location = ToJsonLocation(expr.Location, sourcePath)
        },
        TaskGroupStatement taskGroup => new
        {
            kind = "taskgroup",
            taskGroup.Name,
            statements = taskGroup.Statements.Select(child => ToJsonFunctionStatement(child, sourcePath)),
            location = ToJsonLocation(taskGroup.Location, sourcePath)
        },
        UsingStatement usingStatement => new
        {
            kind = "using",
            usingStatement.ResourceName,
            statements = usingStatement.Statements.Select(child => ToJsonFunctionStatement(child, sourcePath)),
            location = ToJsonLocation(usingStatement.Location, sourcePath)
        },
        SpawnStatement spawn => new
        {
            kind = "spawn",
            spawn.Name,
            expression = ToJsonExpression(spawn.Expression, sourcePath),
            location = ToJsonLocation(spawn.Location, sourcePath)
        },
        AllStatement all => new
        {
            kind = "all",
            all.Targets,
            location = ToJsonLocation(all.Location, sourcePath)
        },
        MatchStatement match => new
        {
            kind = "match",
            scrutinee = match.Scrutinee,
            arms = match.Arms.Select(arm => new
            {
                arm.Pattern,
                arm.Body,
                location = ToJsonLocation(arm.Location, sourcePath)
            }),
            location = ToJsonLocation(match.Location, sourcePath)
        },
        _ => new
        {
            kind = "unknown",
            location = ToJsonLocation(statement.Location, sourcePath)
        }
    };

static object ToJsonExpression(ExpressionNode expression, string sourcePath) => new
{
    expression.Text,
    expression.StaticType,
    location = ToJsonLocation(expression.Location, sourcePath)
};

static object ToJsonModelCall(ModelCallDeclaration modelCall, string sourcePath) => new
{
    modelCall.Provider,
    modelCall.Model,
    modelCall.Task,
    modelCall.Input,
    modelCall.OutputSchema,
    modelCall.Replay,
    modelCall.CapturesProvenance,
    location = ToJsonLocation(modelCall.Location, sourcePath),
    outputSchemaReference = modelCall.OutputSchemaReference is null
        ? null
        : new
        {
            modelCall.OutputSchemaReference.Name,
            location = ToJsonLocation(modelCall.OutputSchemaReference.Location, sourcePath)
        },
    modelCall.Provenance
};

static object ToJsonSchemaMetadata(AISharpDocument document, string schemaName, string sourcePath)
{
    var references = document.Functions
        .SelectMany(function => function.ModelCalls
            .Where(modelCall => modelCall.OutputSchemaReference is not null
                && string.Equals(modelCall.OutputSchemaReference.Name, schemaName, StringComparison.Ordinal))
            .Select(modelCall => new
            {
                function = function.Name,
                modelCall.Provider,
                modelCall.Model,
                modelCall.Task,
                location = ToJsonLocation(modelCall.OutputSchemaReference!.Location, sourcePath)
            }))
        .ToArray();

    return new
    {
        usedAsModelOutput = references.Length > 0,
        modelOutputReferenceCount = references.Length,
        modelOutputReferences = references
    };
}

static object ToJsonLocation(SourceLocation location, string sourcePath) => new
{
    SourcePath = sourcePath,
    location.Line,
    location.Column,
    location.Offset
};

static CommandResult Failure(string command, string message, int exitCode, Stopwatch stopwatch)
{
    var diagnostics = new object[]
    {
        new
        {
            code = "AI3000",
            severity = "error",
            message,
            location = (object?)null
        }
    };
    return new CommandResult(
        exitCode,
        CreateResultPayload(command, false, diagnostics, [], stopwatch));
}

static CommandResult FromCompilation(
    string command,
    CompilationResult compilation,
    IReadOnlyList<ArtifactInfo> artifacts,
    int exitCode,
    Stopwatch stopwatch,
    IReadOnlyDictionary<string, object?>? extra = null)
{
    var baseExtra = new Dictionary<string, object?>
    {
        ["document"] = BuildDocumentPayload(compilation, compilation.SourcePath)
    };

    if (extra is not null)
    {
        foreach (var kv in extra)
            baseExtra[kv.Key] = kv.Value;
    }

    var payload = CreateResultPayload(
        command,
        exitCode == ExitSuccess,
        ToJsonDiagnostics(compilation),
        artifacts,
        stopwatch,
        baseExtra);
    return new CommandResult(exitCode, payload);
}

/// <summary>
/// Writes a provenance record to the file store in the given output directory.
/// Returns the store path, or null on failure (non-fatal).
/// </summary>
static string? WriteProvenanceRecord(string outDir, ProvenanceRecord record)
{
    try
    {
        var provenanceDir = Path.Combine(outDir, ".aisharp", "provenance");
        using var store = new FileProvenanceStore(provenanceDir);
        store.AppendAsync(record).GetAwaiter().GetResult();
        return store.StorePath;
    }
    catch
    {
        return null;
    }
}

/// <summary>Returns the SHA-256 hash of a file as a hex string prefixed with "sha256:".</summary>
static string ComputeFileHash(string filePath)
{
    var bytes = SHA256.HashData(File.ReadAllBytes(filePath));
    return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
}

sealed record CommandResult(int ExitCode, object Payload);

sealed record AcceptanceGateResult(string Name, bool Passed, string? Detail, string? LogPath);

sealed record ArtifactInfo(string Kind, string Path);

sealed record CompilationResult(
    string SourcePath,
    ParseResult ParseResult,
    SemanticResult SemanticResult,
    HirBuildResult HirResult,
    MirBuildResult MirResult);
