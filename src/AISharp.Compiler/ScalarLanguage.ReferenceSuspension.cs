namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private IReadOnlyList<ScalarStatement> PrepareReferenceSlots(IReadOnlyList<ScalarStatement> body, out List<ScalarStatement> slots)
        {
            var aliases = new HashSet<string>(System.StringComparer.Ordinal); var declarations = new List<ScalarStatement>();
            void Collect(IEnumerable<ScalarStatement> statements)
            {
                foreach (var s in statements)
                {
                    if (s.Kind == "ref-local") { aliases.Add(s.Name!); declarations.Add(s); }
                    Collect(s.Body ?? []); Collect(s.Alternative ?? []); if (s.Initializer is { } initializer) Collect([initializer]);
                }
            }
            Collect(body); slots = [];
            if (aliases.Count == 0) return body;
            void Expression(ScalarExpression? e, HashSet<string> live)
            {
                if (e is null) return;
                if (e.Operation == "await" && live.Count != 0) Fail("AI3106", "A reference local cannot remain live across await.", e.Location);
                if (e.Operation == "ref-reassign" && e.Left is { Operation: "local", Value: { } target })
                { live.Remove(target); Expression(e.Right, live); return; }
                if (e.Operation is "reference-store" or "reference-update" && e.Right is not null && HasAwaitExpr(e.Right)) AddAliasUses(e.Left,live);
                if (e.Operation == "call" && e.Arguments is { } passed && passed.Any(HasAwaitExpr))
                    foreach (var argument in passed.Where(a=>ReferenceArgument(a))) AddAliasUses(argument.Left,live);
                if (e.Arguments is { } arguments) foreach (var argument in arguments.Reverse()) Expression(argument, live);
                Expression(e.Right, live); Expression(e.Left, live);
                if (e.Operation == "local" && e.Value is { } name && aliases.Contains(name)) live.Add(name);
            }
            void AddAliasUses(ScalarExpression? e, HashSet<string> live)
            {
                if (e is null) return;
                if (e.Operation == "local" && e.Value is { } name && aliases.Contains(name)) live.Add(name);
                AddAliasUses(e.Left, live); AddAliasUses(e.Right, live); if (e.Arguments is { } args) foreach (var argument in args) AddAliasUses(argument, live);
            }
            HashSet<string> Block(IReadOnlyList<ScalarStatement> statements, HashSet<string> after, HashSet<string>? breaks = null, HashSet<string>? continues = null)
            {
                var live = new HashSet<string>(after);
                foreach (var s in statements.Reverse()) live = Statement(s, live, breaks, continues);
                return live;
            }
            HashSet<string> Statement(ScalarStatement s, HashSet<string> after, HashSet<string>? breaks, HashSet<string>? continues)
            {
                var live = new HashSet<string>(after);
                switch (s.Kind)
                {
                    case "return": case "throw": case "yieldbreak": live.Clear(); break;
                    case "break": return new(breaks ?? after);
                    case "continue": return new(continues ?? after);
                    case "ref-local": live.Remove(s.Name!); break;
                    case "yield": if (live.Count != 0) Fail("AI3106", "A reference local cannot remain live across yield return.", s.Location); break;
                    case "block": return Block(s.Body ?? [], live, breaks, continues);
                    case "switch": return Block(s.Body ?? [], live, after, continues);
                    case "if":
                        live = Block(s.Body ?? [], live, breaks, continues); live.UnionWith(Block(s.Alternative ?? [], after, breaks, continues)); break;
                    case "while": case "dowhile": case "for":
                    {
                        var head = new HashSet<string>(after);
                        for (var iteration = 0; iteration <= aliases.Count + 1; iteration++)
                        {
                            var next = new HashSet<string>(head); Expression(s.Increment,next); Expression(s.Expression,next);
                            var before = Block(s.Body ?? [], next, after, next); before.UnionWith(after); Expression(s.Expression,before);
                            if (head.SetEquals(before)) break; head.UnionWith(before);
                        }
                        return s.Initializer is null ? head : Statement(s.Initializer,head,breaks,continues);
                    }
                    case "try":
                    {
                        var cleanup = (s.Alternative ?? []).FirstOrDefault(c=>c.Kind=="finally");
                        var final = cleanup is null ? after : Block(cleanup.Body ?? [],after,breaks,continues);
                        var exceptional = new HashSet<string>(final);
                        foreach(var handler in (s.Alternative ?? []).Where(c=>c.Kind=="catch"))exceptional.UnionWith(Block(handler.Body ?? [],final,breaks,continues));
                        return Block(s.Body ?? [],exceptional,breaks,continues);
                    }
                }
                Expression(s.Expression,live); return live;
            }
            Block(body,[]);
            slots = declarations.Select(s=>s with { Kind="ref-local-slot",Expression=null }).ToList();
            ScalarStatement Rewrite(ScalarStatement s)
            {
                if(s.Kind=="ref-local")return new("expression",s.Location,new ScalarExpression("ref-reassign",s.Type,s.Location,Left:new("local",s.Type,s.Location,s.Name),Right:s.Expression));
                return s with { Body=s.Body?.Select(Rewrite).ToList(),Alternative=s.Alternative?.Select(Rewrite).ToList(),Initializer=s.Initializer is null?null:Rewrite(s.Initializer) };
            }
            return body.Select(Rewrite).ToList();
        }
    }
}
