using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AISharp.Lowering;

/// <summary>Native build entry point. The caller supplies an initialized MSVC environment.</summary>
internal static class NativeBuildCommand
{
    public static int Run(string[] args)
    {
        object? payload = null;
        int Fail(string message, int code = 1) { payload = new { success = false, error = message }; return code; }
        int Build()
        {
            if (args.Length == 0 || args[0].StartsWith('-'))
                return Fail("Usage: build-native <source> --out <new.exe> --bits <32|64> --cc <absolute cl.exe> [--optimize] [--json]");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var optimized = false;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--json") continue;
                if (args[i] == "--optimize") { optimized = true; continue; }
                if (args[i] is not ("--out" or "--bits" or "--cc") || i + 1 == args.Length || !options.TryAdd(args[i], args[++i]))
                    return Fail("Unknown, duplicate or missing native build option.");
            }
            if (!options.TryGetValue("--bits", out var width) || width is not ("32" or "64") ||
                !options.TryGetValue("--out", out var destination) || !options.TryGetValue("--cc", out var compiler))
                return Fail("Explicit --out, --bits and --cc are required.");
            if (!Path.IsPathFullyQualified(compiler) || !File.Exists(compiler) || !Path.GetFileName(compiler).Equals("cl.exe", StringComparison.OrdinalIgnoreCase))
                return Fail("--cc must name an installed absolute MSVC cl.exe path.");
            var sourcePath = Path.GetFullPath(args[0]);
            destination = Path.GetFullPath(destination);
            if (!destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || File.Exists(destination) || Directory.Exists(destination))
                return Fail("--out must be a new .exe file; existing artifacts are preserved.");
            var source = File.ReadAllText(sourcePath);
            var preparation = NativeProgramCompiler.Prepare(source, int.Parse(width, System.Globalization.CultureInfo.InvariantCulture));
            if (!preparation.Success)
            {
                payload = new { success = false, sourcePath, preparation.SourceSha256, diagnostics = preparation.Diagnostics.Select(d => new {
                    code = d.Code, severity = d.Severity.ToString().ToLowerInvariant(), message = d.Message,
                    location = new { path = sourcePath, line = d.Location.Line, column = d.Location.Column, offset = d.Location.Offset }
                }) };
                return 2;
            }
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, "aisharp-native-" + Guid.NewGuid().ToString("N"));
            var cPath = stem + ".c";
            var objPath = stem + ".obj";
            try
            {
                File.WriteAllText(cPath, preparation.CSource, new UTF8Encoding(false));
                var start = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/nologo", "/TC", "/std:c11", "/W4", "/WX", optimized ? "/O2" : "/Od",
                    cPath, "/Fo" + objPath, "/Fe" + destination, "/link", "/MANIFEST:EMBED", "/MANIFESTUAC:level='asInvoker' uiAccess='false'" })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Native compiler did not start.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(180_000)) { process.Kill(true); process.WaitForExit(); return Fail("Native compiler timed out.", 3); }
                var log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                if (process.ExitCode != 0) { payload = new { success = false, compilerExit = process.ExitCode, log }; return 3; }
                using (var reader = new BinaryReader(File.OpenRead(destination)))
                {
                    if (reader.ReadUInt16() != 0x5a4d) return Fail("Native compiler did not produce PE output.", 3);
                    reader.BaseStream.Position = 0x3c;
                    var pe = reader.ReadInt32();
                    reader.BaseStream.Position = pe;
                    if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != (width == "32" ? 0x14c : 0x8664))
                        return Fail("Native toolchain architecture differs from --bits.", 3);
                }
                payload = new { success = true, capability = NativeProgramCompiler.Capability, sourcePath, preparation.SourceSha256,
                    pointerBits = preparation.PointerBits, optimized, artifact = destination,
                    artifactSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant(),
                    cSourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preparation.CSource!))).ToLowerInvariant() };
                return 0;
            }
            finally
            {
                // Only unique files created by this invocation; no recursive or destination cleanup.
                foreach (var path in new[] { cPath, objPath }) if (File.Exists(path)) File.Delete(path);
            }
        }
        int exit;
        try { exit = Build(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { exit = Fail(error.Message); }
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return exit;
    }
}
