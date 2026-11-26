using System.Diagnostics;
using System.Text;

namespace AISharp.Tests.Harness;

// ─── PR-16 Generated Project Build Harness ────────────────────────────────────

/// <summary>
/// Result of invoking <see cref="GeneratedProjectBuildHarness.Build"/>.
/// Captures the build process exit information, the project file paths,
/// and (on success) the produced assembly path so callers can inspect it.
/// </summary>
public sealed record GeneratedProjectBuildResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string ProjectDirectory,
    string ProjectFilePath,
    string GeneratedSourcePath,
    string? GeneratedAssemblyPath,
    bool Preserved)
{
    /// <summary>
    /// Human-readable diagnostic block suitable for inclusion in test failure
    /// messages. Includes captured stdout, stderr, exit code, and the
    /// preserved project directory (if the debug env var requested preservation).
    /// </summary>
    public string FormatDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Build harness exit code: {ExitCode}");
        sb.AppendLine($"Project directory:      {ProjectDirectory}");
        sb.AppendLine($"Project file:           {ProjectFilePath}");
        sb.AppendLine($"Generated source file:  {GeneratedSourcePath}");
        if (GeneratedAssemblyPath is not null)
        {
            sb.AppendLine($"Generated assembly:     {GeneratedAssemblyPath}");
        }
        sb.AppendLine($"Artifacts preserved:    {Preserved}");
        sb.AppendLine("--- dotnet build STDOUT ---");
        sb.AppendLine(StandardOutput);
        sb.AppendLine("--- dotnet build STDERR ---");
        sb.AppendLine(StandardError);
        return sb.ToString();
    }
}

/// <summary>
/// Test-only harness that builds AI# generated C# inside an isolated temporary
/// .NET project. The harness:
/// <list type="bullet">
///   <item>creates a deterministic temporary project directory,</item>
///   <item>writes the generated C# alongside a minimal <c>.csproj</c>
///         that references <c>AISharp.Runtime</c> by absolute path,</item>
///   <item>runs <c>dotnet build</c> with shared compilation disabled so the
///         process exits cleanly,</item>
///   <item>captures stdout, stderr, exit code, and the produced assembly
///         path on success, and</item>
///   <item>cleans up the temporary directory unless the
///         <c>AISHARP_PRESERVE_BUILD_HARNESS_ARTIFACTS</c> environment
///         variable is set to <c>1</c>/<c>true</c>.</item>
/// </list>
/// The harness never touches the network: it only invokes the locally
/// installed <c>dotnet</c> SDK against a project reference inside this
/// repository.
/// </summary>
internal static class GeneratedProjectBuildHarness
{
    /// <summary>
    /// Environment variable that preserves the temporary project directory
    /// after the build completes. Set to <c>1</c> or <c>true</c> to inspect
    /// the lowered C# and MSBuild output during debugging.
    /// </summary>
    public const string PreserveArtifactsVariable = "AISHARP_PRESERVE_BUILD_HARNESS_ARTIFACTS";

    private const int DefaultBuildTimeoutMilliseconds = 180_000;

    /// <summary>
    /// Compiles <paramref name="generatedCSharpSource"/> in an isolated
    /// temporary .NET project that references <c>AISharp.Runtime</c>.
    /// </summary>
    /// <param name="generatedCSharpSource">
    /// The lowered C# produced by <c>AISharpLowering.Lower</c>.
    /// </param>
    /// <param name="testName">
    /// Short identifier used in the temporary project name. Sanitised so it
    /// is safe for use as a file system name and as an MSBuild AssemblyName.
    /// </param>
    /// <param name="timeoutMilliseconds">
    /// Optional override for the maximum build time. Defaults to 180s.
    /// </param>
    public static GeneratedProjectBuildResult Build(
        string generatedCSharpSource,
        string testName = "sample",
        int timeoutMilliseconds = DefaultBuildTimeoutMilliseconds)
    {
        if (generatedCSharpSource is null)
        {
            throw new ArgumentNullException(nameof(generatedCSharpSource));
        }

        var safeName = SanitizeProjectName(testName);
        var runtimeProjectPath = LocateRuntimeProject();

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"aisharp-build-harness-{safeName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var projectFilePath = Path.Combine(tempRoot, $"{safeName}.csproj");
        var generatedSourcePath = Path.Combine(tempRoot, "Generated.cs");

        var preserve = ShouldPreserveArtifacts();

        try
        {
            File.WriteAllText(projectFilePath, BuildCsproj(safeName, runtimeProjectPath));
            File.WriteAllText(generatedSourcePath, generatedCSharpSource);

            var (exitCode, stdout, stderr) = RunDotnetBuild(projectFilePath, timeoutMilliseconds);
            var succeeded = exitCode == 0;

            string? assemblyPath = null;
            if (succeeded)
            {
                var candidate = Path.Combine(tempRoot, "bin", "Debug", "net9.0", $"{safeName}.dll");
                if (File.Exists(candidate))
                {
                    assemblyPath = candidate;
                }
            }

            return new GeneratedProjectBuildResult(
                Succeeded: succeeded,
                ExitCode: exitCode,
                StandardOutput: stdout,
                StandardError: stderr,
                ProjectDirectory: tempRoot,
                ProjectFilePath: projectFilePath,
                GeneratedSourcePath: generatedSourcePath,
                GeneratedAssemblyPath: assemblyPath,
                Preserved: preserve);
        }
        finally
        {
            if (!preserve)
            {
                TryDeleteDirectory(tempRoot);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunDotnetBuild(
        string projectFilePath,
        int timeoutMilliseconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectFilePath}\" --nologo -v:m " +
                        "-p:UseSharedCompilation=false " +
                        "-p:GenerateFullPaths=true",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Force deterministic, network-free restore behavior.
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet build process.");

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            process.WaitForExit(5_000);
            throw new TimeoutException(
                $"dotnet build did not complete within {timeoutMilliseconds} ms for project '{projectFilePath}'. " +
                $"STDOUT:\n{stdoutBuilder}\nSTDERR:\n{stderrBuilder}");
        }

        // Ensure all async output is flushed before reading.
        process.WaitForExit();

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static string BuildCsproj(string assemblyName, string runtimeProjectPath)
    {
        // Use Nullable=disable so generated AI# C# (which does not yet
        // emit nullable annotations) cannot fail on nullable warnings, and
        // disable TreatWarningsAsErrors so unrelated style warnings do not
        // mask real lowering bugs.
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <OutputType>Library</OutputType>
                <AssemblyName>{assemblyName}</AssemblyName>
                <RootNamespace>{assemblyName}</RootNamespace>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                <UseSharedCompilation>false</UseSharedCompilation>
                <GenerateAssemblyInfo>true</GenerateAssemblyInfo>
                <Deterministic>true</Deterministic>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{runtimeProjectPath}" />
              </ItemGroup>
            </Project>
            """;
    }

    private static string LocateRuntimeProject()
    {
        var configured = Environment.GetEnvironmentVariable("AISHARP_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var project = Path.Combine(configured, "src", "AISharp.Runtime", "AISharp.Runtime.csproj");
            if (File.Exists(project)) return Path.GetFullPath(project);
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src", "AISharp.Runtime", "AISharp.Runtime.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate AISharp.Runtime.csproj relative to the test output directory. " +
            "The build harness requires the repository to be checked out alongside the test assembly.");
    }

    private static bool ShouldPreserveArtifacts()
    {
        var value = Environment.GetEnvironmentVariable(PreserveArtifactsVariable);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "sample";
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        }
        var sanitized = sb.ToString().Trim('_', '-');
        if (sanitized.Length == 0)
        {
            return "sample";
        }
        if (char.IsDigit(sanitized[0]))
        {
            sanitized = "s_" + sanitized;
        }
        return sanitized;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; do not mask the build outcome.
        }
    }
}
