using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AISharp.Runtime;

// ─── Risk level ───────────────────────────────────────────────────────────────

/// <summary>Estimated risk level for an AI-generated patch proposal.</summary>
public enum PatchProposalRiskLevel
{
    /// <summary>Low-risk change: documentation, comments, or trivial formatting.</summary>
    Low,
    /// <summary>Medium-risk change: logic, tests, or configuration.</summary>
    Medium,
    /// <summary>High-risk change: security-sensitive, data-model, or API-surface modifications.</summary>
    High
}

// ─── Patch proposal model ─────────────────────────────────────────────────────

/// <summary>A single file change included in a patch proposal.</summary>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="UnifiedDiff">Unified diff for this file in standard diff format.</param>
public sealed record PatchProposalFile(string Path, string UnifiedDiff);

/// <summary>
/// An AI-generated patch proposal produced by a model runtime.
/// Proposals describe changes but never apply them automatically.
/// </summary>
/// <param name="ChangedFiles">Files included in this proposal.</param>
/// <param name="Rationale">Human-readable rationale for the proposed changes.</param>
/// <param name="TestsToRun">Suggested test commands or test names to run after applying the patch.</param>
/// <param name="RiskLevel">Estimated risk level of this proposal.</param>
/// <param name="PolicyAcknowledgements">Explicit acknowledgements of policy considerations from the model.</param>
public sealed record PatchProposal(
    IReadOnlyList<PatchProposalFile> ChangedFiles,
    string Rationale,
    IReadOnlyList<string> TestsToRun,
    PatchProposalRiskLevel RiskLevel,
    IReadOnlyList<string> PolicyAcknowledgements)
{
    /// <summary>
    /// Stable SHA-256 hash over the canonical proposal content.
    /// Used for provenance and replay without storing raw diffs in the provenance record.
    /// </summary>
    public string ProposalHash => ComputeHash();

    private string ComputeHash()
    {
        var sb = new StringBuilder();
        foreach (var f in ChangedFiles)
        {
            sb.Append(f.Path).Append('\0').Append(f.UnifiedDiff).Append('\0');
        }
        sb.Append(Rationale).Append('\0');
        sb.Append(RiskLevel).Append('\0');
        foreach (var t in TestsToRun) sb.Append(t).Append('\0');
        foreach (var p in PolicyAcknowledgements) sb.Append(p).Append('\0');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// ─── Validation ───────────────────────────────────────────────────────────────

/// <summary>Outcome of validating a <see cref="PatchProposal"/> record.</summary>
public sealed record PatchProposalValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    /// <summary>Returns a passing result with no errors.</summary>
    public static PatchProposalValidationResult Pass() =>
        new(true, Array.Empty<string>());
}

/// <summary>Validates <see cref="PatchProposal"/> records against AI# patch proposal rules.</summary>
public static class PatchProposalValidator
{
    /// <summary>
    /// Validates that a patch proposal is structurally complete and policy-safe.
    /// Does not inspect diff content or run external tools.
    /// </summary>
    public static PatchProposalValidationResult Validate(PatchProposal proposal)
    {
        var errors = new List<string>();

        if (proposal.ChangedFiles.Count == 0)
            errors.Add("Patch proposal must include at least one changed file.");

        foreach (var file in proposal.ChangedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                errors.Add("Each changed file must have a non-empty path.");
            if (string.IsNullOrWhiteSpace(file.UnifiedDiff))
                errors.Add($"Changed file '{file.Path}' must include a non-empty unified diff.");
        }

        if (string.IsNullOrWhiteSpace(proposal.Rationale))
            errors.Add("Patch proposal must include a non-empty rationale.");

        if (proposal.TestsToRun.Count == 0)
            errors.Add("Patch proposal must include at least one test to run.");

        if (proposal.PolicyAcknowledgements.Count == 0)
            errors.Add("Patch proposal must include at least one policy acknowledgement.");

        return errors.Count == 0
            ? PatchProposalValidationResult.Pass()
            : new PatchProposalValidationResult(false, errors);
    }
}

// ─── Patch proposal schema ────────────────────────────────────────────────────

/// <summary>
/// The canonical AI# runtime schema for patch proposal JSON payloads.
/// Models must return JSON matching this schema when requested for a patch proposal.
/// </summary>
public static class PatchProposalSchema
{
    /// <summary>Stable schema name used in model requests.</summary>
    public const string SchemaName = "AiSharpPatchProposal";

    /// <summary>The <see cref="RuntimeSchema"/> for registering in a <see cref="SchemaRegistry"/>.</summary>
    public static readonly RuntimeSchema Schema = RuntimeSchema.FromFields(
        SchemaName,
        new (string, string)[]
        {
            ("changedFiles", "array"),
            ("rationale", "string"),
            ("testsToRun", "array"),
            ("riskLevel", "string"),
            ("policyAcknowledgements", "array")
        });
}

// ─── Request and result ───────────────────────────────────────────────────────

/// <summary>
/// Input to a patch proposal request.
/// Describes the problem context and policy requirements, but not any file to modify directly —
/// the model decides which files to change.
/// </summary>
/// <param name="TaskDescription">Natural-language description of the task to solve via a code patch.</param>
/// <param name="ContextPayload">Serialised context payload delivered to the model. Raw secrets must never be included.</param>
/// <param name="PolicyName">AI# policy name that governs this request.</param>
/// <param name="SessionId">Optional session identifier for provenance correlation.</param>
/// <param name="Replay">Optional replay options for deterministic re-runs.</param>
public sealed record PatchProposalRequest(
    string TaskDescription,
    string ContextPayload,
    string PolicyName,
    string? SessionId = null,
    ModelReplayOptions? Replay = null);

/// <summary>Outcome of a patch proposal request.</summary>
public sealed record PatchProposalResult(
    bool Success,
    PatchProposal? Proposal,
    string? ErrorMessage,
    PatchProposalProvenance? Provenance);

// ─── Patch proposal helper ────────────────────────────────────────────────────

/// <summary>
/// Requests an AI-generated patch proposal from a model runtime.
/// The helper enforces that:
/// - A schema is registered and the model output is validated against it.
/// - No file mutations occur; callers must explicitly apply diffs.
/// - Provenance is captured if a store is provided.
/// </summary>
public static class PatchProposalHelper
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Requests a patch proposal and returns the parsed, validated result.
    /// Throws nothing; all failures are reported via <see cref="PatchProposalResult.ErrorMessage"/>.
    /// </summary>
    public static async Task<PatchProposalResult> RequestAsync(
        PatchProposalRequest request,
        IAISharpModelRuntime runtime,
        SchemaRegistry schemaRegistry,
        IProvenanceStore? provenanceStore = null)
    {
        // Ensure the patch proposal schema is registered so output validation works.
        if (!schemaRegistry.Contains(PatchProposalSchema.SchemaName))
            schemaRegistry.Register(PatchProposalSchema.Schema);

        var modelRequest = new ModelRequest(
            Provider: "local",
            Model: "patch-proposer",
            Task: "propose-patch",
            InputPayload: request.ContextPayload,
            SchemaName: PatchProposalSchema.SchemaName,
            ProviderKind: ModelProviderKind.Local,
            PolicyName: request.PolicyName,
            Replay: request.Replay);

        ModelResponse response;
        try
        {
            response = await runtime.ExecuteAsync(modelRequest).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PatchProposalResult(false, null, $"Model runtime error: {ex.Message}", null);
        }

        if (!response.Success || response.OutputPayload is null)
            return new PatchProposalResult(false, null, response.ErrorMessage ?? "Model returned no output.", null);

        // Validate the raw JSON payload against the schema.
        var schemaValidation = schemaRegistry.Validate(PatchProposalSchema.SchemaName, response.OutputPayload);
        if (!schemaValidation.IsValid)
        {
            var errMsg = string.Join("; ", schemaValidation.Errors.Select(e => e.Message));
            return new PatchProposalResult(false, null, $"Schema validation failed: {errMsg}", null);
        }

        // Deserialise the proposal.
        PatchProposal proposal;
        try
        {
            proposal = DeserializeProposal(response.OutputPayload);
        }
        catch (Exception ex)
        {
            return new PatchProposalResult(false, null, $"Failed to deserialise patch proposal: {ex.Message}", null);
        }

        // Structural validation.
        var structuralValidation = PatchProposalValidator.Validate(proposal);
        if (!structuralValidation.IsValid)
        {
            var errMsg = string.Join("; ", structuralValidation.Errors);
            return new PatchProposalResult(false, null, $"Proposal validation failed: {errMsg}", null);
        }

        // Capture provenance — without storing the raw diff content.
        var provenance = new PatchProposalProvenance(
            Timestamp: DateTimeOffset.UtcNow,
            ProposalHash: proposal.ProposalHash,
            ChangedFileCount: proposal.ChangedFiles.Count,
            RiskLevel: proposal.RiskLevel.ToString(),
            WasApplied: false,
            SessionId: request.SessionId);

        if (provenanceStore is not null)
            await provenanceStore.AppendAsync(provenance).ConfigureAwait(false);

        return new PatchProposalResult(true, proposal, null, provenance);
    }

    private static PatchProposal DeserializeProposal(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var changedFiles = root.TryGetProperty("changedFiles", out var cf)
            ? cf.EnumerateArray()
                .Select(f => new PatchProposalFile(
                    f.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                    f.TryGetProperty("unifiedDiff", out var d) ? d.GetString() ?? string.Empty : string.Empty))
                .ToList()
            : new List<PatchProposalFile>();

        var rationale = root.TryGetProperty("rationale", out var rat)
            ? rat.GetString() ?? string.Empty
            : string.Empty;

        var testsToRun = root.TryGetProperty("testsToRun", out var tr)
            ? tr.EnumerateArray().Select(t => t.GetString() ?? string.Empty).ToList()
            : new List<string>();

        var riskLevelStr = root.TryGetProperty("riskLevel", out var rl)
            ? rl.GetString() ?? "Medium"
            : "Medium";

        var riskLevel = Enum.TryParse<PatchProposalRiskLevel>(riskLevelStr, ignoreCase: true, out var parsed)
            ? parsed
            : PatchProposalRiskLevel.Medium;

        var policyAck = root.TryGetProperty("policyAcknowledgements", out var pa)
            ? pa.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToList()
            : new List<string>();

        return new PatchProposal(changedFiles, rationale, testsToRun, riskLevel, policyAck);
    }
}
