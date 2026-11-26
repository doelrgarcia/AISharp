using AISharp.Compiler;

namespace AISharp.LanguageServer;

// ─── Document snapshot ────────────────────────────────────────────────────────

/// <summary>
/// An immutable snapshot of a document at a specific version.
/// Version is null for documents opened without a version number.
/// </summary>
public sealed record DocumentSnapshot(string Uri, string Text, int? Version);

/// <summary>
/// A partial text replacement expressed in 0-based line/character coordinates.
/// </summary>
public sealed record TextChangeRange(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string NewText);

/// <summary>Result of formatting an open document snapshot.</summary>
public sealed record FormattingResult(string Uri, int? Version, string FormattedText, bool Changed);

// ─── Document store ───────────────────────────────────────────────────────────

/// <summary>
/// Thread-safe in-memory store for open document snapshots.
/// Documents are keyed by URI. Only the latest snapshot per URI is retained.
/// </summary>
public sealed class DocumentStore
{
    private readonly Dictionary<string, DocumentSnapshot> _docs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Opens or replaces the document at the given URI.</summary>
    public DocumentSnapshot Open(string uri, string text, int? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(text);

        var snapshot = new DocumentSnapshot(uri, text, version);
        lock (_lock) _docs[uri] = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Applies a full-text change to an open document and increments the version.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="uri"/> is not currently open.
    /// </exception>
    public DocumentSnapshot Change(string uri, string newText, int? newVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(newText);

        lock (_lock)
        {
            if (!_docs.TryGetValue(uri, out var existing))
                throw new InvalidOperationException(
                    $"Cannot change document '{uri}': document is not open.");

            var updated = new DocumentSnapshot(uri, newText,
                newVersion ?? (existing.Version is int v ? v + 1 : null));
            _docs[uri] = updated;
            return updated;
        }
    }

    /// <summary>Closes the document, removing it from the open set.</summary>
    /// <returns>True if the document was open; false if it was not found.</returns>
    public bool Close(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        lock (_lock) return _docs.Remove(uri);
    }

    /// <summary>Returns the current snapshot for the given URI, or null if not open.</summary>
    public DocumentSnapshot? TryGet(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        lock (_lock) return _docs.TryGetValue(uri, out var s) ? s : null;
    }

    /// <summary>Returns all currently open document URIs.</summary>
    public IReadOnlyList<string> OpenUris()
    {
        lock (_lock) return [.. _docs.Keys];
    }

    /// <summary>Total number of open documents.</summary>
    public int Count
    {
        get { lock (_lock) return _docs.Count; }
    }
}

// ─── Language server service ──────────────────────────────────────────────────

/// <summary>
/// Transport-independent AI# language server service.
/// All methods are synchronous and safe to call from any thread.
/// The service owns a <see cref="DocumentStore"/> and delegates diagnostics
/// to the compiler's <see cref="AISharpLanguageServer"/>.
/// Design note: the service supports both full-sync updates via <see cref="ChangeDocument"/>
/// and incremental updates via <see cref="ChangeDocumentIncremental"/>. When incremental
/// updates are impractical, callers can fall back to full-sync changes.
/// </summary>
public sealed class LanguageServerService
{
    private readonly DocumentStore _store;

    /// <param name="store">Optional injected store; a new one is created if not provided.</param>
    public LanguageServerService(DocumentStore? store = null)
    {
        _store = store ?? new DocumentStore();
    }

    /// <summary>The underlying document store (useful for inspection in tests).</summary>
    public DocumentStore Store => _store;

    /// <summary>
    /// Opens a document, replacing any existing snapshot at the same URI.
    /// Returns current diagnostics for the opened text.
    /// </summary>
    public LanguageServerDiagnosticsResult OpenDocument(string uri, string text, int? version = null)
    {
        var snapshot = _store.Open(uri, text, version);
        return GetDiagnosticsForSnapshot(snapshot);
    }

    /// <summary>
    /// Applies a full-text change to an open document.
    /// Returns updated diagnostics.
    /// </summary>
    public LanguageServerDiagnosticsResult ChangeDocument(string uri, string newText, int? newVersion = null)
    {
        var snapshot = _store.Change(uri, newText, newVersion);
        return GetDiagnosticsForSnapshot(snapshot);
    }

    /// <summary>
    /// Applies incremental edits to an open document and returns updated diagnostics.
    /// </summary>
    public LanguageServerDiagnosticsResult ChangeDocumentIncremental(string uri, IReadOnlyList<TextChangeRange> changes, int? newVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(changes);

        var existing = _store.TryGet(uri)
            ?? throw new InvalidOperationException($"Document '{uri}' is not open. Call OpenDocument first.");
        var updatedText = ApplyTextChanges(existing.Text, changes);
        var snapshot = _store.Change(uri, updatedText, newVersion);
        return GetDiagnosticsForSnapshot(snapshot);
    }

    /// <summary>Closes the document. Returns false if it was not open.</summary>
    public bool CloseDocument(string uri)
        => _store.Close(uri);

    /// <summary>
    /// Returns the latest diagnostics for an open document.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the document is not open.</exception>
    public LanguageServerDiagnosticsResult GetDiagnostics(string uri)
    {
        var snapshot = _store.TryGet(uri)
            ?? throw new InvalidOperationException(
                $"Document '{uri}' is not open. Call OpenDocument first.");
        return GetDiagnosticsForSnapshot(snapshot);
    }

    /// <summary>
    /// Returns diagnostics for any text without requiring the document to be open.
    /// Useful for on-the-fly validation.
    /// </summary>
    public LanguageServerDiagnosticsResult GetDiagnosticsForText(string uri, string text, int? version = null)
    {
        var doc = new LanguageServerTextDocument(uri, text, version);
        return AISharpLanguageServer.GetDiagnostics(doc);
    }

    public IReadOnlyList<DocumentSymbol> GetDocumentSymbolsForUri(string uri)
    {
        var snapshot = _store.TryGet(uri)
            ?? throw new InvalidOperationException(
                $"Document '{uri}' is not open. Call OpenDocument first.");
        return EditorIntelligence.GetDocumentSymbols(snapshot.Text);
    }

    /// <summary>
    /// Returns the declaration location for the symbol at the given cursor position in the open document,
    /// or null if no symbol is found.
    /// </summary>
    public LocationResult? GetDefinition(string uri, int line, int character)
    {
        var snapshot = _store.TryGet(uri)
            ?? throw new InvalidOperationException(
                $"Document '{uri}' is not open. Call OpenDocument first.");
        var result = EditorIntelligence.GetDefinition(snapshot.Text, line, character);
        return result is null ? null : new LocationResult(uri, result.Range);
    }

    /// <summary>
    /// Returns all references of the symbol at the given cursor position in the open document.
    /// </summary>
    public IReadOnlyList<LocationResult> GetReferences(string uri, int line, int character)
    {
        var snapshot = _store.TryGet(uri)
            ?? throw new InvalidOperationException(
                $"Document '{uri}' is not open. Call OpenDocument first.");
        return EditorIntelligence.GetReferences(snapshot.Text, line, character)
            .Select(r => new LocationResult(uri, r.Range))
            .ToList();
    }

    /// <summary>
    /// Formats an open document, updates the stored snapshot, and returns the formatted text.
    /// AI# formatting uses four spaces per indentation level.
    /// </summary>
    public FormattingResult FormatDocument(string uri, int? newVersion = null)
    {
        var snapshot = _store.TryGet(uri)
            ?? throw new InvalidOperationException(
                $"Document '{uri}' is not open. Call OpenDocument first.");
        var formatted = AISharpFormatter.Format(snapshot.Text);
        var changed = !string.Equals(snapshot.Text, formatted, StringComparison.Ordinal);
        var updated = changed ? _store.Change(uri, formatted, newVersion) : snapshot;
        return new FormattingResult(uri, updated.Version, formatted, changed);
    }

    public static string ApplyTextChanges(string text, IReadOnlyList<TextChangeRange> changes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
            return text;

        var materialized = changes
            .Select(change => new
            {
                Change = change,
                Start = GetOffset(text, change.StartLine, change.StartCharacter),
                End = GetOffset(text, change.EndLine, change.EndCharacter)
            })
            .OrderByDescending(item => item.Start)
            .ToArray();

        var result = text;
        foreach (var item in materialized)
        {
            if (item.End < item.Start)
                throw new ArgumentOutOfRangeException(nameof(changes), "Change end must not precede change start.");

            result = result.Remove(item.Start, item.End - item.Start)
                .Insert(item.Start, item.Change.NewText ?? string.Empty);
        }

        return result;
    }

    private static LanguageServerDiagnosticsResult GetDiagnosticsForSnapshot(DocumentSnapshot snapshot)
    {
        var doc = new LanguageServerTextDocument(snapshot.Uri, snapshot.Text, snapshot.Version);
        return AISharpLanguageServer.GetDiagnostics(doc);
    }

    private static int GetOffset(string text, int line, int character)
    {
        if (line < 0)
            throw new ArgumentOutOfRangeException(nameof(line));
        if (character < 0)
            throw new ArgumentOutOfRangeException(nameof(character));

        var currentLine = 0;
        var currentCharacter = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (currentLine == line && currentCharacter == character)
                return index;

            var ch = text[index];
            if (ch == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;

                currentLine++;
                currentCharacter = 0;
                continue;
            }

            if (ch == '\n')
            {
                currentLine++;
                currentCharacter = 0;
                continue;
            }

            currentCharacter++;
        }

        if (currentLine == line && currentCharacter == character)
            return text.Length;

        throw new ArgumentOutOfRangeException(nameof(line), $"Position {line}:{character} is outside the document.");
    }
}
