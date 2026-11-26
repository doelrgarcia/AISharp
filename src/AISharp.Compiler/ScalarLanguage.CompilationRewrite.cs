namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class CompilationBinder
    {
        private void ReadShadows(Unit unit)
        {
            var t = unit.Tokens;
            foreach (var symbol in symbols.Where(s => s.Unit == unit && !s.Function))
            {
                foreach(var capture in Captures(symbol))unit.Shadows.Add(new(symbol.Start,symbol.End,Name(capture.Original),true,capture.Flat));
                var nameIndex = t.ToList().FindIndex(x => x.Location.Offset == symbol.Offset); var after = Next(unit, nameIndex + 1);
                if (t[after].Text == "<")
                {
                    var end = Match(unit, after, "<", ">");
                    for (var k = after + 1; k < end; k++) if (Identifier(t[k]) && t[k].Text is not ("in" or "out")) unit.Shadows.Add(new(symbol.Start, symbol.End, Name(t[k].Text), true));
                }
                var braces = 0; var parens = 0;
                for (var k = nameIndex; k < t.Count && Offset(unit, k) < symbol.End; k++)
                {
                    if(t[k].Kind==TokenKind.String)continue;
                    if (t[k].Text == "{") braces++; else if (t[k].Text == "}") braces--; else if (t[k].Text == "(") parens++; else if (t[k].Text == ")") parens--;
                    if (braces == 1 && parens == 0 && t[k].Text == ":" && k > 0 && Identifier(t[k - 1]) && (k < 2 || t[k - 2].Text != ":")) unit.Shadows.Add(new(symbol.Start, symbol.End, Name(t[k - 1].Text), false));
                }
            }
            for (var i = 0; i < t.Count - 1; i++)
            {
                if(t[i].Kind==TokenKind.String)continue;
                if (t[i].Text != "fn") continue;
                var name = Next(unit, i + 1); var cursor = Next(unit, name + 1); var typeNames = new List<string>();
                if (t[cursor].Text == "<")
                {
                    var close = Match(unit, cursor, "<", ">"); typeNames.AddRange(t.Skip(cursor + 1).Take(close - cursor - 1).Where(Identifier).Select(x => Name(x.Text))); cursor = Next(unit, close + 1);
                }
                if (t[cursor].Text != "(") continue;
                var parameterEnd = Match(unit, cursor, "(", ")"); var end = parameterEnd + 1;
                while (end < t.Count - 1 && t[end].Text is not ("{" or "}" or ";") && t[end].Text != "fn") end++;
                var hasBody=t[end].Text=="{";var bodyEnd = hasBody?Match(unit, end, "{", "}"):end; var from = Offset(unit, i); var until = Offset(unit, bodyEnd)+(hasBody?1:0);
                foreach (var type in typeNames) unit.Shadows.Add(new(from, until, type, true));
                for (var k = cursor + 1; k < parameterEnd; k++) if (t[k].Text == ":" && k > 0 && Identifier(t[k - 1])) unit.Shadows.Add(new(from, until, Name(t[k - 1].Text), false));
                if(!hasBody)continue;
                var blocks = new Stack<(int Start, int End)>();
                for (var k = end; k < bodyEnd; k++)
                {
                    if(t[k].Kind==TokenKind.String)continue;
                    if (t[k].Text == "{") blocks.Push((Offset(unit, k), Offset(unit, Match(unit, k, "{", "}")) + 1));
                    if (t[k].Text == "}" && blocks.Count != 0) blocks.Pop();
                    var next = Next(unit, k + 1);
                    if (t[k].Text is "let" or "var" or "const" && Identifier(t[next]) || t[k].Text is "from" or "join" or "into" && Identifier(t[next]))
                    { var block = blocks.Count == 0 ? (from, until) : blocks.Peek(); unit.Shadows.Add(new(block.Item1, block.Item2, Name(t[next].Text), false)); }
                    if (t[k].Text == "in" && k > end && Identifier(t[k - 1]))
                    { var block = blocks.Count == 0 ? (from, until) : blocks.Peek(); unit.Shadows.Add(new(block.Item1, block.Item2, Name(t[k - 1].Text), false)); }
                }
            }
        }
        private bool Covered(Unit unit, int at) => unit.Edits.Any(e => e.Start <= at && at < e.End);
        private void Replace(Unit unit, int start, int end, string text)
        {
            if (unit.Input.Source[start..end] != text) unit.Edits.Add(new(start, end, text));
        }
        private void Rewrite(Unit unit)
        {
            var t = unit.Tokens;
            foreach (var token in t)
                if (Identifier(token) && Name(token.Text).StartsWith("__unit_", StringComparison.Ordinal)) Fail(unit, token.Location.Offset, "The compilation-group symbol prefix is reserved.");
            foreach (var symbol in symbols.Where(s => s.Unit == unit))
            {
                foreach(var modifier in t.Where(x=>x.Location.Offset>=symbol.Start&&x.Location.Offset<symbol.Offset&&x.Text=="file"))Mask(unit,modifier.Location.Offset,modifier.Location.Offset+modifier.Text.Length);
                var token = t.First(x => x.Location.Offset == symbol.Offset);
                if(!RewriteCapturedDeclaration(unit,symbol))Replace(unit, symbol.Offset, symbol.Offset + token.Text.Length, Internal(symbol));
                unit.Edits.Add(new(symbol.Start, symbol.Start, "\n"));
            }
            for (var i = 0; i < t.Count - 1; i++)
            {
                var token = t[i]; var at = token.Location.Offset;
                if (token.Kind==TokenKind.String||Covered(unit, at)) continue;
                var scope = ScopeAt(unit, at); var previous = i > 0 ? t[i - 1].Text : "";
                if(token.Text=="nameof"&&t[i+1].Text=="("&&Identifier(t[i+2])&&t[i+3].Text==")"&&!Shadowed(unit,at,Name(t[i+2].Text),false)&&ParameterReplacement(unit,at,Name(t[i+2].Text)) is {} captured)
                    Replace(unit,Offset(unit,i+2),Offset(unit,i+2)+t[i+2].Text.Length,captured);
                var typeStart = token.Text is ":" or "->" or "new" or "is" or "as" || token.Text is "typeof" or "default" or "sizeof" && t[i + 1].Text == "(";
                if (typeStart)
                {
                    if (token.Text == ":" && (previous == ":" || t[i + 1].Text == ":")) continue;
                    var begin = Next(unit, i + 1); if (t[begin].Text == "(") begin = Next(unit, begin + 1);
                    var end = begin; var bound = TypeSyntax(unit, t, ref end, scope, Offset(unit, begin), directives: true, strict: false);
                    if (end > begin) { Replace(unit, Offset(unit, begin), Offset(unit, end - 1) + t[end - 1].Text.Length, bound); i = end - 1; }
                    if(token.Text==":"&&symbols.Any(s=>s.Unit==unit&&!s.Function&&s.Offset<at&&s.End>at&& !t.Any(x=>x.Location.Offset>=s.Offset&&x.Location.Offset<at&&x.Text=="{")))
                    {
                        var cursor=end>begin?end:begin+1;
                        while(t[cursor].Text==",")
                        {
                            var next=Next(unit,cursor+1);var stop=next;var syntax=TypeSyntax(unit,t,ref stop,scope,Offset(unit,next),directives:true,strict:false);
                            if(stop==next)break;
                            Replace(unit,Offset(unit,next),Offset(unit,stop-1)+t[stop-1].Text.Length,syntax);cursor=stop;i=stop-1;
                        }
                    }
                    continue;
                }
                if(token.Text=="("&&previous!="nameof"&&i+1<t.Count&&Identifier(t[i+1]))
                {
                    var end=i+1;var syntax=TypeSyntax(unit,t,ref end,scope,Offset(unit,i+1),directives:true,strict:false);
                    var after=end+1<t.Count?t[end+1]:t[^1];var castNext=Identifier(after)||after.Kind==TokenKind.String||after.Text is "(" or "!" or "~"||after.Text.Length>0&&char.IsAsciiDigit(after.Text[0]);
                    if(end>i+1&&t[end].Text==")"&&(!Shadowed(unit,at,Name(t[i+1].Text),false)||castNext)) {Replace(unit,Offset(unit,i+1),Offset(unit,end-1)+t[end-1].Text.Length,syntax);i=end-1;continue;}
                }
                // Expression identifiers retain their source spelling. The scalar binder resolves
                // namespace/type/function qualifiers after local and inherited member lookup.
            }
        }
    }
}
