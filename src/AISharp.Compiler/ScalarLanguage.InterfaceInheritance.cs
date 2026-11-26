namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static string SignatureType(string text)
    {
        // Primitive aliases share signature identity in overrides, interface slots and
        // generic members. Preserve escaped identifiers and source-owned qualified names.
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?:global::)?" + IdentifierSpelling.Pattern + @"(?:\." + IdentifierSpelling.Pattern + @")+",
            match => QualifiedBuiltinAlias(match.Value) ?? match.Value);
        text = System.Text.RegularExpressions.Regex.Replace(text, IdentifierSpelling.Pattern, match =>
        {
            var raw = match.Value;
            if (raw[0] == '@' || IdentifierName(raw) != raw) return raw;
            var before = match.Index - 1; var after = match.Index + match.Length;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
            if (before >= 0 && text[before] == '.' || after < text.Length && text[after] == '.'
                || before >= 1 && text[before] == ':' && text[before - 1] == ':'
                || after + 1 < text.Length && text[after] == ':' && text[after + 1] == ':') return raw;
            return raw == "void" ? "unit" : raw is "float" or "double" ? "f64"
                : ScalarConstants.FromSpelling(raw) is { } primitive ? ScalarConstants.Spelling(primitive) : raw;
        });
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "").Replace(';', ',');
    }

    // Keep direct interface edges in metadata. Consumers traverse the graph, preserving
    // declaration identity through a diamond and avoiding synthetic duplicate slots.
    private static IEnumerable<int> InterfaceClosure(IReadOnlyList<ScalarStruct> all, IEnumerable<int> roots)
    {
        var seen = new HashSet<int>(); var pending = new Stack<int>(roots.Reverse());
        while (pending.TryPop(out var index))
        {
            if (!seen.Add(index)) continue;
            yield return index;
            foreach (var parent in all[index].Interfaces.Reverse()) pending.Push(parent);
        }
    }

    private static void ValidateInterfaceHierarchy()
    {
        var all = activeStructs!; var state = new byte[all.Count];
        void Visit(int index)
        {
            if (state[index] == 2) return;
            if (state[index] == 1) throw DeclarationError("AI3106", $"Circular interface dependency involving '{all[index].Name}'.", all[index].Location);
            state[index] = 1;
            foreach (var parent in all[index].Interfaces) Visit(parent);
            state[index] = 2;
        }
        for (var i = 0; i < all.Count; i++) if (all[i].IsInterface) Visit(i);
    }

    private static IReadOnlyList<ScalarStructMethod> InterfaceMethods(IEnumerable<int> interfaces, string name)
    {
        var all = activeStructs!; var found = new Dictionary<string, ScalarStructMethod>(StringComparer.Ordinal);
        var visited = new HashSet<int>();
        void Visit(int index)
        {
            if (!visited.Add(index)) return;
            var own = all[index].Methods.FirstOrDefault(m => !m.IsConstructor && !m.IsAccessor && m.Name == name);
            if (own is not null) { found.TryAdd(own.QualifiedName, own); return; }
            foreach (var parent in all[index].Interfaces) Visit(parent);
        }
        foreach (var index in interfaces) Visit(index);
        return found.Values.Where(method => !found.Values.Any(other => other.Owner != method.Owner
            && NominalDerives(all, other.Owner, method.Owner))).ToArray();
    }
}
