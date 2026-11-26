using AISharp.Compiler;

namespace AISharp.LanguageServer;

public sealed record DocumentSymbol(string Name, string Kind, LanguageServerRange Range, string? Detail = null);

public sealed record HoverResult(string Contents, LanguageServerRange? Range);

public sealed record CompletionItem(string Label, string Kind, string? Detail = null, string? InsertText = null);

/// <summary>A source location range used for go-to-definition and find-references results.</summary>
public sealed record LocationResult(string? Uri, LanguageServerRange Range);

public static class EditorIntelligence
{
    private static readonly (string Label, string Kind)[] KeywordCompletions =
    [
        ("intent", "keyword"),
        ("policy", "keyword"),
        ("schema", "keyword"),
        ("fn", "keyword"),
        ("tool", "keyword"),
        ("actor", "keyword"),
        ("effect", "keyword"),
        ("type", "keyword"),
        ("resource", "keyword"),
        ("capability", "keyword"),
        ("module", "keyword"),
        ("use", "keyword"),
        ("uses", "keyword"),
        ("test", "keyword"),
        ("contracts", "keyword"),
        ("requires", "keyword"),
        ("ensures", "keyword"),
        ("invariant", "keyword"),
        ("raises", "keyword"),
        ("using", "keyword"),
        ("return", "keyword"),
        ("let", "keyword"),
        ("await", "keyword"),
        ("match", "keyword"),
        ("spawn", "keyword"),
        ("taskgroup", "keyword"),
        ("observe", "keyword"),
        ("handler", "keyword")
    ];

    private static readonly string[] PolicyFieldNames =
    [
        "classification",
        "inference",
        "training",
        "telemetry",
        "external_text",
        "capabilities"
    ];

    /// <summary>Built-in effect names known to the AI# compiler.</summary>
    private static readonly string[] KnownEffectNames =
    [
        "io", "time", "random", "model", "network", "unsafe_ffi"
    ];

    /// <summary>Contract keywords surfaced as completion items.</summary>
    private static readonly string[] ContractKeywords =
    [
        "requires", "ensures", "invariant", "raises"
    ];

    public static IReadOnlyList<DocumentSymbol> GetDocumentSymbols(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = AISharpParser.Parse(text).Document;
        var symbols = new List<DocumentSymbol>();

        if (document.ModuleDeclaration is { } module)
            symbols.Add(CreateSymbol(module.Name, "module", module.Location, $"module {module.Name}"));

        symbols.AddRange(document.Policies.Select(policy =>
            CreateSymbol(policy.Name, "policy", policy.Location, BuildPolicyDetail(policy))));
        symbols.AddRange(document.Schemas.Select(schema =>
            CreateSymbol(schema.Name, "schema", schema.Location, BuildSchemaDetail(schema))));
        symbols.AddRange(document.Functions.Select(function =>
            CreateSymbol(function.Name, "function", function.Location, BuildFunctionDetail(function))));
        symbols.AddRange(document.Tools.Select(tool =>
            CreateSymbol(tool.Name, "tool", tool.Location, BuildToolDetail(tool))));

        foreach (var actor in document.Actors)
        {
            symbols.Add(CreateSymbol(actor.Name, "actor", actor.Location, $"actor {actor.Name}"));
            foreach (var handler in actor.Handlers)
            {
                symbols.Add(CreateSymbol(
                    $"{actor.Name}.{handler.Name}",
                    "actor_handler",
                    handler.Location,
                    BuildFunctionDetail(handler)));
            }
        }

        symbols.AddRange(document.Types.Select(type =>
            CreateSymbol(type.Name, "type", type.Location, BuildTypeDetail(type))));
        symbols.AddRange(document.Resources.Select(resource =>
            CreateSymbol(resource.Name, "resource", resource.Location, $"resource {resource.Name}")));
        symbols.AddRange(document.EffectDeclarations.Select(effect =>
            CreateSymbol(effect.Name, "effect", effect.Location, $"effect {effect.Name}")));
        symbols.AddRange(document.Capabilities.Select(capability =>
            CreateSymbol(capability.Name, "capability", capability.Location, $"capability {capability.Name}")));
        symbols.AddRange(document.Tests.Select(test =>
            CreateSymbol(test.Name, "test", test.Location, $"test {test.Name}")));

        return symbols;
    }

    public static HoverResult? GetHover(string text, int line, int character)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = AISharpParser.Parse(text).Document;
        var word = ExtractWordAt(text, line, character);

        var symbol = GetDocumentSymbols(text)
            .FirstOrDefault(candidate => Contains(candidate.Range, line, character))
            ?? (word is null ? null : GetDocumentSymbols(text)
                .FirstOrDefault(candidate => candidate.Name == word || candidate.Name.EndsWith("." + word, StringComparison.Ordinal)));

        if (symbol is null)
            return null;

        var start = symbol.Range.Start;
        var contents = BuildHoverContents(document, symbol, start);
        return new HoverResult(contents, symbol.Range);
    }

    public static IReadOnlyList<CompletionItem> GetCompletions(string text, int line, int character)
    {
        ArgumentNullException.ThrowIfNull(text);

        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (label, kind) in KeywordCompletions)
            AddCompletion(items, seen, new CompletionItem(label, kind));

        foreach (var effectName in KnownEffectNames)
            AddCompletion(items, seen, new CompletionItem(effectName, "effect", "built-in effect"));

        foreach (var contractKeyword in ContractKeywords)
            AddCompletion(items, seen, new CompletionItem(contractKeyword, "keyword", "contract keyword"));

        var document = AISharpParser.Parse(text).Document;
        foreach (var symbol in GetDocumentSymbols(text))
            AddCompletion(items, seen, new CompletionItem(symbol.Name, "symbol", symbol.Kind));

        if (IsInsidePolicyBlock(text, line))
        {
            foreach (var fieldName in PolicyFieldNames)
                AddCompletion(items, seen, new CompletionItem(fieldName, "keyword", "policy field"));
        }

        // In-scope parameter completions when inside a function or actor handler body.
        var enclosingFunction = FindEnclosingFunction(document, line);
        if (enclosingFunction is not null)
        {
            foreach (var parameter in enclosingFunction.Parameters)
            {
                AddCompletion(items, seen, new CompletionItem(
                    parameter.Name,
                    "parameter",
                    $"{parameter.Name}: {parameter.Type}"));
            }
        }

        return items;
    }

    private static FunctionDeclaration? FindEnclosingFunction(AISharpDocument document, int line)
    {
        // Source locations are 1-based; LSP positions are 0-based.
        var oneBasedLine = line + 1;

        FunctionDeclaration? best = null;
        var bestStart = int.MinValue;

        foreach (var function in document.Functions)
        {
            if (function.Location.Line <= oneBasedLine && function.Location.Line > bestStart)
            {
                best = function;
                bestStart = function.Location.Line;
            }
        }

        foreach (var actor in document.Actors)
        {
            foreach (var handler in actor.Handlers)
            {
                if (handler.Location.Line <= oneBasedLine && handler.Location.Line > bestStart)
                {
                    best = handler;
                    bestStart = handler.Location.Line;
                }
            }
        }

        return best;
    }

    private static string BuildHoverContents(AISharpDocument document, DocumentSymbol symbol, LanguageServerPosition start)
    {
        var location = $"Declared at {start.Line + 1}:{start.Character + 1}";

        switch (symbol.Kind)
        {
            case "function":
            {
                var fn = document.Functions.FirstOrDefault(f => f.Name == symbol.Name);
                if (fn is null) break;
                return BuildFunctionHover(fn, location);
            }
            case "actor_handler":
            {
                var handler = document.Actors
                    .SelectMany(a => a.Handlers.Select(h => new { Actor = a, Handler = h }))
                    .FirstOrDefault(x => $"{x.Actor.Name}.{x.Handler.Name}" == symbol.Name);
                if (handler is null) break;
                return BuildFunctionHover(handler.Handler, location, qualifier: $"{handler.Actor.Name}.");
            }
            case "tool":
            {
                var tool = document.Tools.FirstOrDefault(t => t.Name == symbol.Name);
                if (tool is null) break;
                var lines = new List<string>
                {
                    $"**tool** `{symbol.Name}`",
                    "",
                    "```aisharp",
                    BuildToolDetail(tool),
                    "```",
                    "",
                    location
                };
                return string.Join("\n", lines);
            }
            case "policy":
            {
                var policy = document.Policies.FirstOrDefault(p => p.Name == symbol.Name);
                if (policy is null) break;
                var fields = policy.Fields.Count == 0
                    ? "(no fields)"
                    : string.Join("\n", policy.Fields.Select(f => $"- `{f.Key}` = `{f.Value}`"));
                return $"**policy** `{symbol.Name}`\n\n{fields}\n\n{location}";
            }
            case "schema":
            {
                var schema = document.Schemas.FirstOrDefault(s => s.Name == symbol.Name);
                if (schema is null) break;
                var fields = schema.Fields.Count == 0
                    ? "(no fields)"
                    : string.Join("\n", schema.Fields.Select(f => $"- `{f.Name}: {f.Type}`"));
                return $"**schema** `{symbol.Name}`\n\n{fields}\n\n{location}";
            }
            case "type":
            {
                var type = document.Types.FirstOrDefault(t => t.Name == symbol.Name);
                if (type is null) break;
                var detail = BuildTypeDetail(type);
                return $"**type** `{symbol.Name}`\n\n```aisharp\n{detail}\n```\n\n{location}";
            }
            case "actor":
            {
                var actor = document.Actors.FirstOrDefault(a => a.Name == symbol.Name);
                if (actor is null) break;
                var handlerList = actor.Handlers.Count == 0
                    ? "(no handlers)"
                    : string.Join("\n", actor.Handlers.Select(h => $"- `{h.Name}`"));
                return $"**actor** `{symbol.Name}`\n\nhandlers:\n{handlerList}\n\n{location}";
            }
        }

        return $"**{symbol.Kind}** `{symbol.Name}`\n\n{symbol.Detail ?? string.Empty}\n\n{location}".Trim();
    }

    private static string BuildFunctionHover(FunctionDeclaration fn, string location, string qualifier = "")
    {
        var signature = BuildFunctionDetail(fn, qualifier);
        var contracts = fn.Contracts.Count == 0
            ? "(none)"
            : string.Join("\n", fn.Contracts.Select(c => $"- `{c.Kind.ToString().ToLowerInvariant()} {c.Expression}`"));
        var effects = fn.Effects.Count == 0
            ? "(none)"
            : string.Join(", ", fn.Effects.Select(e => $"`{e}`"));

        return string.Join("\n",
            $"**function** `{qualifier}{fn.Name}`",
            "",
            "```aisharp",
            signature,
            "```",
            "",
            $"**effects:** {effects}",
            "",
            "**contracts:**",
            contracts,
            "",
            location);
    }

    private static void AddCompletion(List<CompletionItem> items, HashSet<string> seen, CompletionItem item)
    {
        if (seen.Add(item.Label))
            items.Add(item);
    }

    private static bool Contains(LanguageServerRange range, int line, int character) =>
        line == range.Start.Line
        && character >= range.Start.Character
        && character <= range.End.Character;

    /// <summary>
    /// Returns the declaration location for the symbol at the given cursor position,
    /// or null if no symbol is found at that position.
    /// </summary>
    public static LocationResult? GetDefinition(string text, int line, int character)
    {
        ArgumentNullException.ThrowIfNull(text);

        var word = ExtractWordAt(text, line, character);
        if (string.IsNullOrEmpty(word))
            return null;

        var declaration = GetDocumentSymbols(text)
            .FirstOrDefault(s => s.Name == word);

        return declaration is null ? null : new LocationResult(null, declaration.Range);
    }

    /// <summary>
    /// Returns all references (declaration + usages) of the symbol at the given cursor position
    /// within the current document.
    /// </summary>
    public static IReadOnlyList<LocationResult> GetReferences(string text, int line, int character)
    {
        ArgumentNullException.ThrowIfNull(text);

        var word = ExtractWordAt(text, line, character);
        if (string.IsNullOrEmpty(word))
            return [];

        return FindAllOccurrences(text, word);
    }

    /// <summary>
    /// Returns all document locations where <paramref name="symbolName"/> appears as a whole word.
    /// </summary>
    public static IReadOnlyList<LocationResult> FindAllOccurrences(string text, string symbolName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(symbolName);

        if (symbolName.Length == 0)
            return [];

        var results = new List<LocationResult>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal)
                        .Split('\n');

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var lineText = lines[lineIdx];
            var startSearch = 0;
            while (true)
            {
                var idx = lineText.IndexOf(symbolName, startSearch, StringComparison.Ordinal);
                if (idx < 0) break;

                var before = idx > 0 && (char.IsLetterOrDigit(lineText[idx - 1]) || lineText[idx - 1] == '_');
                var after = idx + symbolName.Length < lineText.Length
                    && (char.IsLetterOrDigit(lineText[idx + symbolName.Length]) || lineText[idx + symbolName.Length] == '_');

                if (!before && !after)
                {
                    var startPos = new LanguageServerPosition(lineIdx, idx);
                    var endPos = new LanguageServerPosition(lineIdx, idx + symbolName.Length);
                    results.Add(new LocationResult(null, new LanguageServerRange(startPos, endPos)));
                }

                startSearch = idx + 1;
            }
        }

        return results;
    }

    /// <summary>Extracts the identifier word at the given zero-based line/character position.</summary>
    internal static string? ExtractWordAt(string text, int line, int character)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal)
                        .Split('\n');
        if (line < 0 || line >= lines.Length)
            return null;

        var lineText = lines[line];
        if (character < 0 || character > lineText.Length)
            return null;

        var start = character;
        while (start > 0 && IsWordChar(lineText[start - 1]))
            start--;

        var end = character;
        while (end < lineText.Length && IsWordChar(lineText[end]))
            end++;

        return start == end ? null : lineText[start..end];
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static DocumentSymbol CreateSymbol(string name, string kind, SourceLocation location, string? detail = null)
    {
        var start = new LanguageServerPosition(Math.Max(0, location.Line - 1), Math.Max(0, location.Column - 1));
        var end = new LanguageServerPosition(start.Line, start.Character + Math.Max(1, name.Length));
        return new DocumentSymbol(name, kind, new LanguageServerRange(start, end), detail);
    }

    private static string BuildPolicyDetail(PolicyDeclaration policy)
    {
        if (policy.Fields.Count == 0)
            return $"policy {policy.Name}";
        var fields = string.Join(", ", policy.Fields.Select(f => $"{f.Key}={f.Value}"));
        return $"policy {policy.Name} {{ {fields} }}";
    }

    private static string BuildSchemaDetail(SchemaDeclaration schema)
    {
        var fields = string.Join(", ", schema.Fields.Select(f => $"{f.Name}: {f.Type}"));
        return $"schema {schema.Name} = {{ {fields} }}";
    }

    private static string BuildFunctionDetail(FunctionDeclaration fn, string qualifier = "")
    {
        var parameters = string.Join(", ", fn.Parameters.Select(FormatParameter));
        var keyword = fn.IsAsync ? "async fn" : "fn";
        var line = $"{keyword} {qualifier}{fn.Name}({parameters}) -> {fn.ReturnType}";
        if (fn.Effects.Count > 0)
            line += $" effects [{string.Join(", ", fn.Effects)}]";
        return line;
    }

    private static string FormatParameter(ParameterDeclaration parameter) =>
        parameter.Ownership switch
        {
            ParameterOwnership.Owned => $"{parameter.Name}: owned {parameter.Type}",
            ParameterOwnership.Borrowed => $"{parameter.Name}: borrowed {parameter.Type}",
            _ => $"{parameter.Name}: {parameter.Type}"
        };

    private static string BuildToolDetail(ToolDeclaration tool)
    {
        var parameters = string.Join(", ", tool.Parameters.Select(FormatParameter));
        var line = $"tool {tool.Name}({parameters}) -> {tool.ReturnType}";
        if (!string.IsNullOrEmpty(tool.Sandbox))
            line += $" sandbox={tool.Sandbox}";
        if (tool.Capabilities.Count > 0)
            line += $" capabilities=[{string.Join(", ", tool.Capabilities)}]";
        if (!string.IsNullOrEmpty(tool.Timeout))
            line += $" timeout={tool.Timeout}";
        return line;
    }

    private static string BuildTypeDetail(TypeDeclaration type)
    {
        if (type.Kind == TypeKind.Union)
            return $"type {type.Name} = {string.Join(" | ", type.Cases)}";
        var fields = string.Join(", ", type.Fields.Select(f => $"{f.Name}: {f.Type}"));
        return $"type {type.Name} = {{ {fields} }}";
    }

    private static bool IsInsidePolicyBlock(string text, int targetLine)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');
        var currentPolicyStart = -1;

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("policy ", StringComparison.Ordinal) && trimmed.Contains('{'))
                currentPolicyStart = index;
            else if (currentPolicyStart >= 0 && trimmed == "}")
            {
                if (targetLine >= currentPolicyStart && targetLine <= index)
                    return true;

                currentPolicyStart = -1;
            }
        }

        return currentPolicyStart >= 0 && targetLine >= currentPolicyStart;
    }
}
