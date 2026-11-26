namespace AISharp.Compiler;

public enum CompilerPipelineStage
{
    SourceText,
    Ast,
    Hir,
    TypedMir,
    LoweredTarget
}

public enum LoweredArtifactTarget
{
    RoslynCSharp
}

public enum CompilerBoundaryContext
{
    CurrentTaskSlice
}

public interface IAstArtifact;

public interface IHirArtifact;

public interface ITypedMirArtifact;

public interface ILoweredArtifact;

public sealed record SourceMapReference(
    string SourcePath,
    int Line,
    int Column);

public sealed record LoweredArtifactMetadata
{
    public LoweredArtifactMetadata(
        LoweredArtifactTarget target,
        string generatedPath,
        SourceMapReference sourceMap,
        bool requiresAnalyzerMetadata,
        IReadOnlyList<string> provenanceMetadataKeys,
        IReadOnlyList<string> analyzerMetadataKeys)
    {
        Target = target;
        GeneratedPath = generatedPath;
        SourceMap = sourceMap;
        RequiresAnalyzerMetadata = requiresAnalyzerMetadata;
        ProvenanceMetadataKeys = provenanceMetadataKeys;
        AnalyzerMetadataKeys = analyzerMetadataKeys;
    }

    public LoweredArtifactTarget Target { get; init; }

    public string GeneratedPath { get; init; }

    public SourceMapReference SourceMap { get; init; }

    public bool RequiresAnalyzerMetadata { get; init; }

    public IReadOnlyList<string> ProvenanceMetadataKeys { get; init; }

    public IReadOnlyList<string> AnalyzerMetadataKeys { get; init; }
}

public sealed record RoslynLoweredArtifact(
    string CSharpSource,
    LoweredArtifactMetadata Metadata) : ILoweredArtifact;

public enum ReleaseIntegrityGateKind
{
    Build,
    Test,
    Provenance,
    Sbom,
    Signing,
    Publish
}

public sealed record ReleaseIntegrityGate(
    string Name,
    ReleaseIntegrityGateKind Kind,
    IReadOnlyList<string> RequiredEvidenceKeys,
    string BlockingFailureMode);

public sealed record ReleaseArtifactMetadata(
    string Path,
    string ArtifactType,
    string Version,
    IReadOnlyList<FileHashMetadata> Hashes);

public sealed record BuildEvidenceMetadata(
    string Command,
    string Configuration,
    int ExitCode,
    IReadOnlyList<FileHashMetadata> ArtifactHashes);

public sealed record TestEvidenceMetadata(
    string Command,
    int Passed,
    int Failed);

public sealed record ReleaseProvenanceMetadata(
    string Format,
    string BuilderId,
    string SourceRevision,
    string SubjectDigest,
    string Path);

public sealed record SbomMetadata(
    string Format,
    string SpecVersion,
    string Path,
    string SubjectDigest);

public sealed record SigningMetadata(
    string Provider,
    string Identity,
    string TransparencyLog,
    string SignaturePath);

public sealed record ReleaseIntegrityEnvelope(
    ReleaseArtifactMetadata Artifact,
    BuildEvidenceMetadata Build,
    TestEvidenceMetadata Tests,
    ReleaseProvenanceMetadata Provenance,
    SbomMetadata Sbom,
    SigningMetadata Signing,
    IReadOnlyList<ReleaseIntegrityGate> Gates);

public sealed class RoslynAnalyzerHook : IEquatable<RoslynAnalyzerHook>
{
    public RoslynAnalyzerHook(
        string diagnosticId,
        string metadataKey,
        string responsibility,
        CompilerPipelineStage stage,
        IReadOnlyList<string> requiredMetadataKeys)
    {
        DiagnosticId = diagnosticId;
        MetadataKey = metadataKey;
        Responsibility = responsibility;
        Stage = stage;
        RequiredMetadataKeys = requiredMetadataKeys;
    }

    public string DiagnosticId { get; init; }

    public string MetadataKey { get; init; }

    public string Responsibility { get; init; }

    public CompilerPipelineStage Stage { get; init; }

    public IReadOnlyList<string> RequiredMetadataKeys { get; init; }

    public bool Equals(RoslynAnalyzerHook? other)
    {
        return other is not null
            && DiagnosticId == other.DiagnosticId
            && MetadataKey == other.MetadataKey
            && Responsibility == other.Responsibility
            && Stage == other.Stage
            && RequiredMetadataKeys.SequenceEqual(other.RequiredMetadataKeys);
    }

    public override bool Equals(object? obj) => Equals(obj as RoslynAnalyzerHook);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DiagnosticId);
        hash.Add(MetadataKey);
        hash.Add(Responsibility);
        hash.Add(Stage);
        foreach (var key in RequiredMetadataKeys)
        {
            hash.Add(key);
        }

        return hash.ToHashCode();
    }
}

public sealed record CompilerPipelineBoundary(
    string Name,
    CompilerPipelineStage From,
    CompilerPipelineStage To,
    Type InputContract,
    Type OutputContract,
    CompilerBoundaryContext RequiredContext,
    string Responsibility,
    string HandOffContract);

public static class AISharpCompilerPipeline
{
    public static IReadOnlyList<ReleaseIntegrityGate> ReleaseIntegrityGates { get; } =
    [
        new(
            "build",
            ReleaseIntegrityGateKind.Build,
            ["build.command", "build.exitCode", "build.artifactHashes"],
            "Reject release when the compiler/runtime build fails or artifact hashes are missing."),
        new(
            "tests",
            ReleaseIntegrityGateKind.Test,
            ["test.command", "test.passed", "test.failed"],
            "Reject release when required tests fail or the test evidence is incomplete."),
        new(
            "provenance",
            ReleaseIntegrityGateKind.Provenance,
            ["provenance.format", "provenance.builderId", "provenance.subjectDigest"],
            "Reject release when provenance is absent or does not bind to the artifact digest."),
        new(
            "sbom",
            ReleaseIntegrityGateKind.Sbom,
            ["sbom.format", "sbom.specVersion", "sbom.subjectDigest"],
            "Reject release when a machine-readable SBOM is absent or not tied to the artifact digest."),
        new(
            "signing",
            ReleaseIntegrityGateKind.Signing,
            ["signing.provider", "signing.identity", "signing.signaturePath", "signing.transparencyLog"],
            "Reject release when artifacts are unsigned or transparency-log evidence is missing."),
        new(
            "publish",
            ReleaseIntegrityGateKind.Publish,
            ["release.version", "release.artifactPath", "release.notes"],
            "Reject publish when versioned artifacts and release notes are not represented.")
    ];

    public static IReadOnlyList<RoslynAnalyzerHook> RoslynAnalyzerHooks { get; } =
    [
        new(
            "AI9001",
            "ai.provenance",
            "Require provenance metadata before lowered model-call output is consumed by generated C#",
            CompilerPipelineStage.LoweredTarget,
            ["ai.provenance", "ai.sourceMap"])
    ];

    public static IReadOnlyList<CompilerPipelineBoundary> Boundaries { get; } =
    [
        new(
            "parse-source-to-ast",
            CompilerPipelineStage.SourceText,
            CompilerPipelineStage.Ast,
            typeof(string),
            typeof(AISharpDocument),
            CompilerBoundaryContext.CurrentTaskSlice,
            "Lex and parse AI# source text into the syntax-preserving document model, then run front-end validation diagnostics.",
            "AISharpParser.Parse returns ParseResult with AISharpDocument as the AST artifact plus structured diagnostics."),
        new(
            "build-ast-to-hir",
            CompilerPipelineStage.Ast,
            CompilerPipelineStage.Hir,
            typeof(IAstArtifact),
            typeof(IHirArtifact),
            CompilerBoundaryContext.CurrentTaskSlice,
            "Resolve document-level declarations, imports, capabilities, resources, effects, and actor/function ownership into a semantic high-level IR.",
            "HIR builders consume only IAstArtifact instances and produce IHirArtifact instances with resolved symbols and validation state."),
        new(
            "check-hir-to-typed-mir",
            CompilerPipelineStage.Hir,
            CompilerPipelineStage.TypedMir,
            typeof(IHirArtifact),
            typeof(ITypedMirArtifact),
            CompilerBoundaryContext.CurrentTaskSlice,
            "Type-check expressions, contracts, model-call schemas, structured concurrency, and effect obligations before optimization or lowering.",
            "Typed MIR builders consume only IHirArtifact instances and produce ITypedMirArtifact instances with explicit types, effects, and checked contracts."),
        new(
            "lower-typed-mir-to-target",
            CompilerPipelineStage.TypedMir,
            CompilerPipelineStage.LoweredTarget,
            typeof(ITypedMirArtifact),
            typeof(ILoweredArtifact),
            CompilerBoundaryContext.CurrentTaskSlice,
            "Lower typed MIR into backend-specific artifacts while preserving diagnostics, provenance hooks, runtime checks, and sandbox/tool boundaries.",
            "Lowering consumes only ITypedMirArtifact instances and produces ILoweredArtifact instances for the selected backend; Roslyn C# artifacts must include source-map, provenance, and analyzer metadata keys required by AISharpCompilerPipeline.RoslynAnalyzerHooks.")
    ];
}
