namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    /// <summary>Checks every normal exit after finally unwinding. Throws and nonterminating paths have no out-value obligation.</summary>
    public static Diagnostic? ValidateReferenceExits(ScalarFunction function, IReadOnlyList<ScalarStruct> layouts)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        void Leaves(string path, ScalarType type, HashSet<ScalarType> seen)
        {
            var index = (int)type - StructTypeBase;
            if (index < 0 || index >= layouts.Count || layouts[index].IsClass || layouts[index].IsTypeParameter || !seen.Add(type)) { required.Add(path); return; }
            for (var i = 0; i < layouts[index].Fields.Count; i++) Leaves(path + "." + i, layouts[index].Fields[i].Type, new(seen));
        }
        foreach (var parameter in function.Parameters.Where(p => p.Mode == ScalarParameterMode.Out)) Leaves(parameter.Name, parameter.Type, []);
        if (required.Count == 0) return null;
        var flow = new ReferenceExitFlow(required);
        var exits = flow.Block(function.Body, new("normal", function.Location, []));
        var bad = exits.FirstOrDefault(e => e.Kind is "normal" or "return" && !required.IsSubsetOf(e.Assigned));
        return bad is null ? null : new("AI3106", DiagnosticSeverity.Error, "Every out parameter must be assigned on every normal return, including after finally cleanup.", bad.At);
    }

    private sealed record ReferenceExit(string Kind, SourceLocation At, HashSet<string> Assigned);
    private sealed class ReferenceExitFlow(HashSet<string> required)
    {
        private static string? Path(ScalarExpression? e) => e is { Operation: "local" } ? e.Value
            : e is { Operation: "field", Left: not null } && Path(e.Left) is { } root ? root + "." + e.Value : null;
        private void Assign(ScalarExpression? e, HashSet<string> state)
        {
            if (Path(e) is not { } path) return;
            state.UnionWith(required.Where(key => key == path || key.StartsWith(path + ".", StringComparison.Ordinal)));
        }
        private static bool? Truth(ScalarExpression? e) => e is { Operation: "literal", Type: ScalarType.Bool } ? e.Value == "true" : null;
        private void Expression(ScalarExpression? e, HashSet<string> state)
        {
            if (e is null) return;
            if (e.Operation is "&&" or "||")
            {
                Expression(e.Left, state); var before = new HashSet<string>(state);
                var truth = Truth(e.Left); var executes = e.Operation == "&&" ? truth : truth is null ? null : !truth;
                if (executes == false) return;
                Expression(e.Right, state); if (executes is null) state.IntersectWith(before); return;
            }
            Expression(e.Left, state); Expression(e.Right, state);
            if (e.Operation == "switch" && e.Arguments is { } arms)
            {
                HashSet<string>? all = null; var unmatched = new HashSet<string>(state);
                for (var i = 0; i + 1 < arms.Count; i += 2)
                {
                    Expression(arms[i], unmatched); if (Truth(arms[i]) == false) continue;
                    var arm = new HashSet<string>(unmatched); Expression(arms[i + 1], arm);
                    if (all is null) all = arm; else all.IntersectWith(arm);
                    if (Truth(arms[i]) == true) break;
                }
                if (all is not null) { state.Clear(); state.UnionWith(all); } return;
            }
            if (e.Arguments is { } arguments) foreach (var argument in arguments) Expression(argument, state);
            if (e.Operation == "=") Assign(e.Left, state);
            if (e.Operation == "call" && e.Arguments is { } passed)
                foreach (var argument in passed.Where(a => a.Operation == "argument-out")) Assign(argument.Left, state);
        }
        private static List<ReferenceExit> Merge(IEnumerable<ReferenceExit> exits)
        {
            var result = new List<ReferenceExit>();
            foreach (var group in exits.GroupBy(e => (e.Kind, e.At.Offset)))
            {
                var state = new HashSet<string>(group.First().Assigned);
                foreach (var exit in group.Skip(1)) state.IntersectWith(exit.Assigned);
                result.Add(group.First() with { Assigned = state });
            }
            return result;
        }
        public List<ReferenceExit> Block(IReadOnlyList<ScalarStatement> statements, ReferenceExit input)
        {
            var states = new List<ReferenceExit> { input };
            foreach (var statement in statements)
            {
                var next = states.Where(s => s.Kind != "normal").ToList();
                foreach (var state in states.Where(s => s.Kind == "normal")) next.AddRange(Statement(statement, state));
                states = Merge(next);
            }
            return states;
        }
        private List<ReferenceExit> Statement(ScalarStatement s, ReferenceExit input)
        {
            var state = new HashSet<string>(input.Assigned);
            if (s.Kind is not ("for" or "while" or "dowhile")) Expression(s.Expression, state);
            ReferenceExit Normal() => new("normal", s.Location, new(state));
            switch (s.Kind)
            {
                case "return": case "throw": case "break": case "continue": return [new(s.Kind, s.Location, state)];
                case "if":
                {
                    var truth = Truth(s.Expression); var result = new List<ReferenceExit>();
                    if (truth != false) result.AddRange(Block(s.Body ?? [], Normal()));
                    if (truth != true) result.AddRange(Block(s.Alternative ?? [], Normal()));
                    return Merge(result);
                }
                case "block": return Block(s.Body ?? [], Normal());
                case "switch": return Merge(Block(s.Body ?? [], Normal()).Select(e => e.Kind == "break" ? e with { Kind = "normal" } : e));
                case "try":
                {
                    var exits = Block(s.Body ?? [], Normal());
                    foreach (var handler in (s.Alternative ?? []).Where(c => c.Kind == "catch")) exits.AddRange(Block(handler.Body ?? [], Normal()));
                    var cleanup = (s.Alternative ?? []).FirstOrDefault(c => c.Kind == "finally");
                    if (cleanup is null) return Merge(exits);
                    var unwound = new List<ReferenceExit>();
                    foreach (var exit in exits)
                        foreach (var after in Block(cleanup.Body ?? [], exit with { Kind = "normal" }))
                            unwound.Add(after.Kind == "normal" ? exit with { Assigned = after.Assigned } : after);
                    return Merge(unwound);
                }
                case "for": case "while": case "dowhile":
                {
                    var start = s.Initializer is null ? new List<ReferenceExit> { Normal() } : Statement(s.Initializer, Normal());
                    var result = start.Where(e => e.Kind != "normal").ToList();
                    foreach (var entry in start.Where(e => e.Kind == "normal"))
                    {
                        var atCondition = new HashSet<string>(entry.Assigned);
                        if (s.Kind != "dowhile") Expression(s.Expression, atCondition);
                        var truth = Truth(s.Expression);
                        if (s.Kind != "dowhile" && truth != true) result.Add(entry with { Assigned = new(atCondition) });
                        if (s.Kind != "dowhile" && truth == false) continue;
                        var body = Block(s.Body ?? [], entry with { Assigned = atCondition });
                        result.AddRange(body.Where(e => e.Kind is "return" or "throw"));
                        result.AddRange(body.Where(e => e.Kind == "break").Select(e => e with { Kind = "normal" }));
                        foreach (var iteration in body.Where(e => e.Kind is "normal" or "continue"))
                        {
                            var after = new HashSet<string>(iteration.Assigned); Expression(s.Increment, after); Expression(s.Expression, after);
                            if (truth != true) result.Add(iteration with { Kind = "normal", Assigned = after });
                        }
                    }
                    return Merge(result);
                }
                default: return [Normal()];
            }
        }
    }
}
