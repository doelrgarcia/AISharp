namespace AISharp.Compiler;

public sealed record LanguageServerTextDocument(
    string Uri,
    string Text,
    int? Version = null);

public sealed record LanguageServerPosition(int Line, int Character);

public sealed record LanguageServerRange(LanguageServerPosition Start, LanguageServerPosition End);

public enum LanguageServerDiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}

public sealed record LanguageServerDiagnostic(
    LanguageServerRange Range,
    LanguageServerDiagnosticSeverity Severity,
    string Code,
    string Source,
    string Message);

public sealed record LanguageServerDiagnosticsResult(
    string Uri,
    int? Version,
    IReadOnlyList<LanguageServerDiagnostic> Diagnostics);

public sealed record LanguageServerCapabilities(
    string LanguageId,
    bool DiagnosticsProvider,
    bool DocumentSymbolProvider,
    IReadOnlyList<string> MetadataKinds);

public static class AISharpLanguageServer
{
    private const string DiagnosticSource = "aisharp";

    public static LanguageServerCapabilities Capabilities { get; } = new(
        "aisharp",
        DiagnosticsProvider: true,
        DocumentSymbolProvider: true,
        MetadataKinds:
        [
            "sourceMap",
            "diagnostics",
            "declarations",
            "effects",
            "contracts",
            "modelCalls",
            "provenance",
            "schemas",
            "tools"
        ]);

    public static LanguageServerDiagnosticsResult GetDiagnostics(LanguageServerTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var parseResult = AISharpParser.Parse(document.Text);
        var diagnostics = parseResult.Diagnostics
            .Select(ToLanguageServerDiagnostic)
            .ToArray();

        return new LanguageServerDiagnosticsResult(document.Uri, document.Version, diagnostics);
    }

    private static LanguageServerDiagnostic ToLanguageServerDiagnostic(Diagnostic diagnostic)
    {
        var startLine = Math.Max(0, diagnostic.Location.Line - 1);
        var startCharacter = Math.Max(0, diagnostic.Location.Column - 1);
        var range = new LanguageServerRange(
            new LanguageServerPosition(startLine, startCharacter),
            new LanguageServerPosition(startLine, startCharacter + 1));

        return new LanguageServerDiagnostic(
            range,
            ToLanguageServerSeverity(diagnostic.Severity),
            diagnostic.Code,
            DiagnosticSource,
            diagnostic.Message);
    }

    private static LanguageServerDiagnosticSeverity ToLanguageServerSeverity(DiagnosticSeverity severity) =>
        severity switch
        {
            DiagnosticSeverity.Error => LanguageServerDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => LanguageServerDiagnosticSeverity.Warning,
            _ => LanguageServerDiagnosticSeverity.Information
        };
}
