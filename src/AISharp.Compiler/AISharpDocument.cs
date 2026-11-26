namespace AISharp.Compiler;

public sealed record ModuleDeclaration(string Name, SourceLocation Location);

public sealed record UseDeclaration(string Path, SourceLocation Location);

public sealed record EffectDeclaration(string Name, SourceLocation Location);

public sealed record AISharpDocument(
    string? Module,
    string? Intent,
    IReadOnlyList<UseDeclaration> Uses,
    IReadOnlyList<PolicyDeclaration> Policies,
    IReadOnlyList<CapabilityDeclaration> Capabilities,
    IReadOnlyList<ResourceDeclaration> Resources,
    IReadOnlyList<string> Effects,
    IReadOnlyList<TypeDeclaration> Types,
    IReadOnlyList<SchemaDeclaration> Schemas,
    IReadOnlyList<ToolDeclaration> Tools,
    IReadOnlyList<MacroDeclaration> Macros,
    IReadOnlyList<GeneratorDeclaration> Generators,
    IReadOnlyList<FunctionDeclaration> Functions,
    IReadOnlyList<ActorDeclaration> Actors,
    IReadOnlyList<TestDeclaration> Tests,
    IReadOnlyList<ObservationDeclaration> Observability) : IAstArtifact
{
    public string Profile { get; init; } = ScalarLanguage.LegacyProfile;
    public IReadOnlyList<ScalarFunction> ScalarFunctions { get; init; } = [];
    public bool ScalarValidationFailed { get; init; }
    public ModuleDeclaration? ModuleDeclaration { get; init; }

    public IReadOnlyList<EffectDeclaration> EffectDeclarations { get; init; } = [];
}

public sealed record PolicyField(string Key, string Value);

public sealed record PolicyDeclaration(string Name, IReadOnlyList<PolicyField> Fields, SourceLocation Location)
{
    public string? Classification => GetFieldValue("classification");

    public string? Inference => GetFieldValue("inference");

    public string? Training => GetFieldValue("training");

    public string? Telemetry => GetFieldValue("telemetry");

    public string? ExternalText => GetFieldValue("external_text");

    public string? Capabilities => GetFieldValue("capabilities");

    private string? GetFieldValue(string key) =>
        Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal))?.Value.Trim();
}

public sealed record CapabilityDeclaration(string Name, SourceLocation Location);

public sealed record ResourceDeclaration(string Name, SourceLocation Location);

public enum TypeKind { Union, Record }

public sealed record TypeDeclaration(
    string Name,
    TypeKind Kind,
    IReadOnlyList<string> Cases,
    IReadOnlyList<ParameterDeclaration> Fields,
    SourceLocation Location);

public sealed record SchemaDeclaration(string Name, IReadOnlyList<ParameterDeclaration> Fields, SourceLocation Location);

public sealed record ToolDeclaration(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    string ReturnType,
    string? Sandbox,
    IReadOnlyList<string> Capabilities,
    string? Timeout,
    SourceLocation Location);

public sealed record CompileTimeBody(string Text, SourceLocation Location);

public sealed record MacroDeclaration(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    string ReturnType,
    string? Input,
    string? Expands,
    CompileTimeBody Body,
    SourceLocation Location);

public sealed record GeneratorDeclaration(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    string ReturnType,
    bool Incremental,
    bool Deterministic,
    IReadOnlyList<string> Tracks,
    string? Output,
    CompileTimeBody Body,
    SourceLocation Location);

public sealed record FunctionDeclaration(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    string ReturnType,
    IReadOnlyList<string> Effects,
    IReadOnlyList<ContractDeclaration> Contracts,
    FunctionBody Body,
    IReadOnlyList<ModelCallDeclaration> ModelCalls,
    SourceLocation Location,
    bool IsAsync = false);

public sealed record ActorStateDeclaration(string Name, string Type, SourceLocation Location);

public sealed record ActorDeclaration(
    string Name,
    IReadOnlyList<ActorStateDeclaration> StateMembers,
    IReadOnlyList<FunctionDeclaration> Handlers,
    SourceLocation Location)
{
    public string RuntimeProfile => "actor";
}

public sealed record FunctionBody(IReadOnlyList<FunctionStatement> Statements)
{
    public ScalarSource? ScalarSource { get; init; }
}

public abstract record FunctionStatement(SourceLocation Location);

public sealed record LetStatement(
    string Name,
    string? TypeAnnotation,
    ExpressionNode Initializer,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record ReturnStatement(
    ExpressionNode Expression,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record ExpressionStatement(
    ExpressionNode Expression,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record TaskGroupStatement(
    string? Name,
    IReadOnlyList<FunctionStatement> Statements,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record SpawnStatement(
    string Name,
    ExpressionNode Expression,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record AllStatement(
    IReadOnlyList<string> Targets,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record UsingStatement(
    string ResourceName,
    IReadOnlyList<FunctionStatement> Statements,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record MatchArm(string Pattern, string Body, SourceLocation Location);

public sealed record MatchStatement(
    string Scrutinee,
    IReadOnlyList<MatchArm> Arms,
    SourceLocation Location) : FunctionStatement(Location);

public sealed record ExpressionNode(string Text, string? StaticType, SourceLocation Location);

public sealed record ModelReplayDeclaration(string Arguments, int? Seed = null, decimal? Temperature = null);

public sealed record ToolInvocationMetadata(
    string Name,
    string Sandbox,
    string? Input,
    string? Output,
    int? ExitCode,
    TimeSpan? Duration);

public sealed record FileHashMetadata(string Path, string Algorithm, string Value);

public sealed record ProvenanceMetadataDeclaration(
    string? PromptTemplateId,
    string Provider,
    string Model,
    ModelReplayDeclaration? Replay,
    IReadOnlyList<ToolInvocationMetadata> ToolInvocations,
    IReadOnlyList<FileHashMetadata> FileHashes,
    bool CaptureEnabled = true);

public sealed record SchemaReferenceDeclaration(string Name, SourceLocation Location);

public sealed record ModelCallDeclaration(
    string Provider,
    string Model,
    string? Task,
    string? Input,
    string? OutputSchema,
    ModelReplayDeclaration? Replay,
    bool CapturesProvenance,
    SourceLocation Location,
    SchemaReferenceDeclaration? OutputSchemaReference = null,
    ProvenanceMetadataDeclaration? Provenance = null);

public enum ParameterOwnership
{
    Unspecified,
    Owned,
    Borrowed
}

public sealed record ParameterDeclaration(string Name, string Type, ParameterOwnership Ownership = ParameterOwnership.Unspecified)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public SourceLocation? NameLocation { get; init; }

    // Diagnostic provenance must not change the established semantic parameter identity.
    public bool Equals(ParameterDeclaration? other) => other is not null
        && Name == other.Name && Type == other.Type && Ownership == other.Ownership;
    public override int GetHashCode() => HashCode.Combine(Name, Type, Ownership);
}

public sealed record ContractDeclaration(ContractKind Kind, string Expression, SourceLocation Location)
{
    public SourceLocation? ExpressionLocation { get; init; }
    public ContractCheckPhase CheckPhase => Kind switch
    {
        ContractKind.Requires => ContractCheckPhase.Precondition,
        ContractKind.Ensures => ContractCheckPhase.Postcondition,
        ContractKind.Invariant => ContractCheckPhase.Invariant,
        _ => throw new InvalidOperationException($"Unsupported contract kind '{Kind}'.")
    };

    public bool EmitRuntimeCheck => true;

    public bool EmitDebugCheck => true;

    public bool EmitVerificationCondition => true;

    public string VerificationHook => Kind switch
    {
        ContractKind.Requires => "requires",
        ContractKind.Ensures => "ensures",
        ContractKind.Invariant => "invariant",
        _ => throw new InvalidOperationException($"Unsupported contract kind '{Kind}'.")
    };
}

public enum ContractKind
{
    Requires,
    Ensures,
    Invariant
}

public enum ContractCheckPhase
{
    Precondition,
    Postcondition,
    Invariant
}

public sealed record TestDeclaration(string Name, SourceLocation Location);

public sealed record ObservationDeclaration(string Kind, string Name, SourceLocation Location);

public sealed record ParseResult(AISharpDocument Document, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
}
