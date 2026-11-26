namespace AISharp.Lowering;

// ─── Debug span ───────────────────────────────────────────────────────────────

/// <summary>A source code span (start + end) in zero-based line/column coordinates.</summary>
public sealed record DebugSpan(int StartLine, int StartColumn, int EndLine, int EndColumn);

// ─── Debug map entry ──────────────────────────────────────────────────────────

/// <summary>
/// A single source-map entry relating an AI# source span to a generated C# span.
/// </summary>
public sealed record DebugMapEntry(
    string MemberName,
    DebugSpan AiSharpSpan,
    DebugSpan GeneratedSpan);

// ─── Debug source map ─────────────────────────────────────────────────────────

/// <summary>
/// Source map produced by the AI# lowering pass.
/// Provides forward mapping (AI# → generated C#) for breakpoint placement,
/// and reverse mapping (generated C# → AI#) for stack-frame display.
/// </summary>
public sealed class DebugSourceMap
{
    private readonly List<DebugMapEntry> _entries;

    /// <summary>Constructs a debug source map from the given entries.</summary>
    public DebugSourceMap(IEnumerable<DebugMapEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
    }

    /// <summary>All entries in source order (AI# source line ascending).</summary>
    public IReadOnlyList<DebugMapEntry> Entries => _entries;

    /// <summary>
    /// Forward mapping: given an AI# source position, returns the best matching
    /// generated C# span, or null if no entry covers that position.
    /// </summary>
    public DebugMapEntry? MapForward(int aiSharpLine, int aiSharpColumn)
    {
        DebugMapEntry? best = null;
        foreach (var entry in _entries)
        {
            var span = entry.AiSharpSpan;
            if (aiSharpLine < span.StartLine || aiSharpLine > span.EndLine)
                continue;
            if (aiSharpLine == span.StartLine && aiSharpColumn < span.StartColumn)
                continue;
            if (aiSharpLine == span.EndLine && aiSharpColumn > span.EndColumn)
                continue;

            // Prefer the narrowest matching span.
            if (best is null ||
                (span.EndLine - span.StartLine) < (best.AiSharpSpan.EndLine - best.AiSharpSpan.StartLine))
                best = entry;
        }
        return best;
    }

    /// <summary>
    /// Reverse mapping: given a generated C# line/column, returns the best matching
    /// AI# source span, or null if no entry covers that generated position.
    /// </summary>
    public DebugMapEntry? MapReverse(int generatedLine, int generatedColumn)
    {
        DebugMapEntry? best = null;
        foreach (var entry in _entries)
        {
            var span = entry.GeneratedSpan;
            if (generatedLine < span.StartLine || generatedLine > span.EndLine)
                continue;
            if (generatedLine == span.StartLine && generatedColumn < span.StartColumn)
                continue;
            if (generatedLine == span.EndLine && generatedColumn > span.EndColumn)
                continue;

            if (best is null ||
                (span.EndLine - span.StartLine) < (best.GeneratedSpan.EndLine - best.GeneratedSpan.StartLine))
                best = entry;
        }
        return best;
    }
}
