using System.Text;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

/// <summary>
/// Deterministic formatter for AI# source text.
/// The formatter uses four spaces per indentation level to match the repository samples.
/// </summary>
public static partial class AISharpFormatter
{
    public const int IndentSize = 4;

    private static readonly string IndentUnit = new(' ', IndentSize);

    public static string Format(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var output = new List<string>(lines.Length);
        var indentLevel = 0;
        var previousWasBlank = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                if (!previousWasBlank && output.Count > 0)
                {
                    output.Add(string.Empty);
                    previousWasBlank = true;
                }

                continue;
            }

            if (trimmed.StartsWith("}", StringComparison.Ordinal))
                indentLevel = Math.Max(0, indentLevel - 1);

            var formatted = NormalizeLine(trimmed);
            var lineIndent = indentLevel + ContinuationIndent(formatted);
            output.Add(string.Concat(Enumerable.Repeat(IndentUnit, lineIndent)) + formatted);
            previousWasBlank = false;

            var (opens, closes) = CountBracesOutsideText(formatted);
            if (trimmed.StartsWith("}", StringComparison.Ordinal) && closes > 0)
                closes--;

            indentLevel = Math.Max(0, indentLevel + opens - closes);
        }

        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);

        return string.Join('\n', output) + "\n";
    }

    private static int ContinuationIndent(string line)
    {
        if (line.StartsWith("requires ", StringComparison.Ordinal)
            || line.StartsWith("ensures ", StringComparison.Ordinal)
            || line.StartsWith("invariant ", StringComparison.Ordinal)
            || line.StartsWith("effects ", StringComparison.Ordinal)
            || line.StartsWith("effect(", StringComparison.Ordinal)
            || line.StartsWith("-> ", StringComparison.Ordinal)
            || line.StartsWith("sandbox ", StringComparison.Ordinal)
            || line.StartsWith("capabilities ", StringComparison.Ordinal)
            || line.StartsWith("timeout ", StringComparison.Ordinal))
        {
            return 1;
        }

        return 0;
    }

    private static string NormalizeLine(string line)
    {
        if (line.StartsWith("//", StringComparison.Ordinal))
            return line;

        var (code, comment) = SplitComment(line);
        var normalized = CollapseWhitespaceOutsideStrings(code.Trim());
        normalized = NormalizePunctuation(normalized);
        normalized = NormalizeDeclarationKeyword(normalized, "module");
        normalized = NormalizeDeclarationKeyword(normalized, "use");
        normalized = NormalizeDeclarationKeyword(normalized, "capability");
        normalized = NormalizeDeclarationKeyword(normalized, "resource");
        normalized = NormalizeDeclarationKeyword(normalized, "effect");
        normalized = NormalizeDeclarationKeyword(normalized, "type");
        normalized = NormalizeDeclarationKeyword(normalized, "schema");
        normalized = NormalizeDeclarationKeyword(normalized, "policy");
        normalized = NormalizeDeclarationKeyword(normalized, "tool");
        normalized = NormalizeDeclarationKeyword(normalized, "fn");
        normalized = NormalizeAsyncFunction(normalized);
        normalized = NormalizeDeclarationKeyword(normalized, "actor");
        normalized = NormalizeDeclarationKeyword(normalized, "state");
        normalized = NormalizeDeclarationKeyword(normalized, "test");
        normalized = NormalizeDeclarationKeyword(normalized, "counter");
        normalized = NormalizeDeclarationKeyword(normalized, "histogram");
        normalized = NormalizeDeclarationKeyword(normalized, "return");
        normalized = NormalizeDeclarationKeyword(normalized, "let");
        normalized = NormalizeDeclarationKeyword(normalized, "requires");
        normalized = NormalizeDeclarationKeyword(normalized, "ensures");
        normalized = NormalizeDeclarationKeyword(normalized, "invariant");
        normalized = NormalizeDeclarationKeyword(normalized, "effects");
        normalized = NormalizeDeclarationKeyword(normalized, "spawn");
        normalized = NormalizeDeclarationKeyword(normalized, "all");
        normalized = NormalizeDeclarationKeyword(normalized, "using");
        normalized = NormalizeDeclarationKeyword(normalized, "match");
        normalized = NormalizeDeclarationKeyword(normalized, "expect");
        normalized = normalized.Trim();

        return comment is null ? normalized : $"{normalized} {comment.Trim()}".TrimEnd();
    }

    private static string NormalizePunctuation(string value)
    {
        var result = value;
        result = SpacesAroundEquals().Replace(result, " = ");
        result = SpacesAroundArrow().Replace(result, " -> ");
        result = SpacesAroundFatArrow().Replace(result, " => ");
        result = SpacesAfterComma().Replace(result, ", ");
        result = SpacesBeforeComma().Replace(result, ",");
        result = SpacesAfterOpenParen().Replace(result, "(");
        result = SpacesBeforeCloseParen().Replace(result, ")");
        result = SpacesInsideOpenBracket().Replace(result, "[");
        result = SpacesInsideCloseBracket().Replace(result, "]");
        result = MultipleSpaces().Replace(result, " ");
        return result.Trim();
    }

    private static string NormalizeDeclarationKeyword(string value, string keyword) =>
        Regex.Replace(value, $"^{Regex.Escape(keyword)}\\s+", keyword + " ", RegexOptions.CultureInvariant);

    private static string NormalizeAsyncFunction(string value) =>
        Regex.Replace(value, "^async\\s+fn\\s+", "async fn ", RegexOptions.CultureInvariant);

    private static string CollapseWhitespaceOutsideStrings(string value)
    {
        var sb = new StringBuilder(value.Length);
        var inString = false;
        var escaped = false;
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (inString)
            {
                sb.Append(ch);
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                pendingSpace = false;
                inString = true;
                sb.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static (string Code, string? Comment) SplitComment(string line)
    {
        var inString = false;
        var escaped = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            var ch = line[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '/' && line[i + 1] == '/')
                return (line[..i], line[i..]);
        }

        return (line, null);
    }

    private static (int Opens, int Closes) CountBracesOutsideText(string line)
    {
        var (code, _) = SplitComment(line);
        var inString = false;
        var escaped = false;
        var opens = 0;
        var closes = 0;

        foreach (var ch in code)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
                opens++;
            else if (ch == '}')
                closes++;
        }

        return (opens, closes);
    }

    [GeneratedRegex("\\s*=\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesAroundEquals();

    [GeneratedRegex("\\s*->\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesAroundArrow();

    [GeneratedRegex("\\s*=>\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesAroundFatArrow();

    [GeneratedRegex(",\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesAfterComma();

    [GeneratedRegex("\\s+,", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesBeforeComma();

    [GeneratedRegex("\\(\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesAfterOpenParen();

    [GeneratedRegex("\\s+\\)", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesBeforeCloseParen();

    [GeneratedRegex("\\[\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesInsideOpenBracket();

    [GeneratedRegex("\\s+\\]", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesInsideCloseBracket();

    [GeneratedRegex(" {2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleSpaces();
}
