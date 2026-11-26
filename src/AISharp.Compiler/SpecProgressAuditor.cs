namespace AISharp.Compiler;

public enum SpecProgressStatus
{
    Complete,
    InProgress,
    Blocked
}

public sealed record SpecProgressAuditResult(
    int CompletedChecklistItems,
    int IncompleteChecklistItems,
    IReadOnlyList<string> Blockers,
    SpecProgressStatus Status)
{
    public int TotalChecklistItems => CompletedChecklistItems + IncompleteChecklistItems;
}

public static class SpecProgressAuditor
{
    public static SpecProgressAuditResult Audit(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var completed = 0;
        var incomplete = 0;
        var blockers = new List<string>();

        foreach (var line in EnumerateLines(markdown))
        {
            var trimmedStart = line.TrimStart();

            if (trimmedStart.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase))
            {
                completed++;
            }
            else if (trimmedStart.StartsWith("- [ ]", StringComparison.Ordinal))
            {
                incomplete++;
            }

            if (line.Contains("BLOCKED:", StringComparison.Ordinal))
            {
                blockers.Add(line.Trim());
            }
        }

        var status = blockers.Count > 0
            ? SpecProgressStatus.Blocked
            : incomplete == 0 && completed > 0
                ? SpecProgressStatus.Complete
                : SpecProgressStatus.InProgress;

        return new SpecProgressAuditResult(completed, incomplete, blockers, status);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
