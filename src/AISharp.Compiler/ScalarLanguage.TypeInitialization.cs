namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static bool activeTypeInitialization;
    // Async support is synthesized before declarations are extracted. Detect initializer syntax in
    // lexer tokens so older programs retain the identical exception dispatcher and generated C.
    private static bool UsesTypeInitialization(string source, ScalarFeatures features)
    {
        var tokens = new Lexer(source).Lex().Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text).ToArray();
        if (tokens.Contains("TypeInitializationException", StringComparer.Ordinal)) return true;
        if ((features & ScalarFeatures.StaticInitialization) == 0) return false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] != "static") continue;
            var j = i + 1;
            while (j < tokens.Length && tokens[j] is "public" or "private" or "internal" or "readonly" or "required") j++;
            if (j + 1 >= tokens.Length) continue;
            if (tokens[j] == "fn" && tokens[j + 1] == "new") return true;
            if (tokens[j] == "event") j++;
            if (j + 1 >= tokens.Length) continue;
            if (tokens[j + 1] != ":") continue;
            for (j += 2; j < tokens.Length; j++)
            {
                if (tokens[j] == "=") return true;
                if (tokens[j] == "{")
                {
                    var depth = 1;
                    while (++j < tokens.Length && depth > 0) { if (tokens[j] == "{") depth++; else if (tokens[j] == "}") depth--; }
                    if (j < tokens.Length && tokens[j] == "=") return true;
                    break;
                }
                if (tokens[j] is ";" or "}" or "fn" or "static" or "public" or "private" or "internal" or "readonly" or "event") break;
            }
        }
        return false;
    }

    private static ScalarStructMethod ImplicitStaticConstructor(ScalarStruct owner, int index) =>
        new("<cctor>", owner.Name + ".<cctor>", false, false,
            new FunctionDeclaration("new", [], "unit", [], [], new FunctionBody([]) { ScalarSource = new("", owner.Location) }, [], owner.Location))
        { Owner = index, IsStatic = true, IsStaticConstructor = true, IsImplicitStaticConstructor = true };

    private sealed partial class ExtendedBinder
    {
        // Preserve source text and its original span: initializer binding happens after all signatures exist.
        private ScalarSource ReadStaticInitializer()
        {
            NewLines(); var start = Current.Location; var depth = 0;
            while (Current.Text != "")
            {
                if (depth == 0 && Current.Text is ";" or "\n" or "}") break;
                var token = Next();
                if (token.Text is "(" or "[" or "{") depth++;
                else if (token.Text is ")" or "]" or "}")
                {
                    if (--depth < 0) Fail("AI3106", "Unbalanced static field initializer.", token.Location);
                }
            }
            if (depth != 0 || Current.Location.Offset == start.Offset) Fail("AI3106", "Expected a complete static field initializer.", start);
            var a = start.Offset - _declarationStart.Offset; var b = Current.Location.Offset - _declarationStart.Offset;
            return new(_declaration![a..b], start);
        }

        private IReadOnlyList<ScalarStatement> BindStaticInitializers()
        {
            var statements = new List<ScalarStatement>(); var savedTokens = _tokens; var savedPosition = _position;
            foreach (var initializer in _struct!.StaticInitializers)
            {
                var assigned = new HashSet<string>(_assigned); var patterns = new HashSet<string>(_patternLocals);
                _scopes.Add(new(StringComparer.Ordinal));
                try
                {
                    var source = initializer.Expression;
                    _tokens = Lex(source.Text, source.Location, Has(ScalarFeatures.Floats) || Has(ScalarFeatures.Decimals), Has(ScalarFeatures.Chars), Has(ScalarFeatures.Strings)); _position = 0;
                    var expression = Expression(); Read(expression); NewLines();
                    if (Current.Text != "") Fail("AI3106", "Unexpected token in static field initializer.");
                    var field = StaticFieldRead(_struct, new(initializer.Field, source.Location), allowHidden: true);
                    WriteStatic(field); expression = Coerce(expression, field.Type);
                    statements.AddRange(_pendingDeclarations); _pendingDeclarations.Clear();
                    statements.Add(new("expression", source.Location, new("=", field.Type, source.Location, Left: field, Right: expression)));
                }
                finally
                {
                    _scopes.RemoveAt(_scopes.Count - 1); _assigned = assigned;
                    _patternLocals.Clear(); foreach (var name in patterns) _patternLocals.Add(name);
                    _tokens = savedTokens; _position = savedPosition;
                }
            }
            return statements;
        }
    }
}
