namespace AISharp.Compiler;

public enum DiagnosticSeverity
{
    Error,
    Warning
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourceLocation Location);
