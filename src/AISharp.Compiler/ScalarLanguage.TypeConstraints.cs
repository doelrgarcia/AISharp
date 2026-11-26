namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<int, GenericInfo>? typeGenerics;
    private sealed record TypeUse(int Template, IReadOnlyList<ScalarType> Arguments, SourceLocation At, bool DeclarationChecked);
    [ThreadStatic] private static List<TypeUse>? typeUses;
    [ThreadStatic] private static HashSet<string>? typeUseKeys;
    [ThreadStatic] private static bool typeConstraintsReady;

    private static bool UsesConstructorConstraints(string source)
    {
        var tokens = new Lexer(source).Lex().Where(t => t.Kind is not (TokenKind.String or TokenKind.NewLine)).ToArray();
        var clause = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind is TokenKind.LBrace or TokenKind.RBrace) clause = false;
            if (tokens[i].Kind == TokenKind.Identifier && tokens[i].Text == "where" && i + 2 < tokens.Length
                && tokens[i + 1].Kind == TokenKind.Identifier && tokens[i + 2].Kind == TokenKind.Colon) clause = true;
            if (clause && tokens[i].Text == "new" && i + 2 < tokens.Length
                && tokens[i + 1].Kind == TokenKind.LParen && tokens[i + 2].Kind == TokenKind.RParen) return true;
        }
        return false;
    }

    private sealed partial class ExtendedBinder
    {
        private void BindTypeConstraints(GenericInfo definition)
        {
            NewLines();
            while (Take("where"))
            {
                var name = Next(); var parameter = definition.Parameters.FirstOrDefault(p => p.Name == name.Name);
                if (parameter is null) Fail("AI3106", $"'{name.Text}' is not a type parameter of '{definition.Name}'.", name.Location);
                if (parameter!.HasWhere) Fail("AI3106", $"A constraint clause has already been specified for type parameter '{name.Text}'.", name.Location);
                parameter.HasWhere = true; Expect(":"); NewLines();
                do
                {
                    NewLines(); var token = HeaderTypeToken(); var constraint = token.Text;
                    if (!IsIdentifier(constraint.Split('<')[0]) && QualifiedBuiltinAlias(constraint) is null || constraint is "where" or "void" or "unit") Fail("AI3106", "Expected a type constraint.", token.Location);
                    if (constraint == "new") { Expect("("); Expect(")"); constraint = "new()"; }
                    if (constraint is "notnull" or "default") Fail("AI3100", $"The '{constraint}' constraint is not supported yet.", token.Location);
                    if (constraint is "class" or "struct" or "unmanaged" && parameter.Constraints.Count > 0)
                        Fail("AI3106", $"The '{constraint}' constraint must come before any other constraints.", token.Location);
                    if (parameter.Constraints.Any(c => c.Text == "new()")) Fail("AI3106", "The 'new()' constraint must be last.", token.Location);
                    if (constraint == "new()" && parameter.Constraints.Any(c => c.Text is "struct" or "unmanaged"))
                        Fail("AI3106", "The 'new()' constraint cannot be combined with 'struct' or 'unmanaged'.", token.Location);
                    if (parameter.Constraints.Any(c => c.Text == constraint)) Fail("AI3106", $"Duplicate constraint '{constraint}' for type parameter '{name.Text}'.", token.Location);
                    parameter.Constraints.Add((constraint, token.Location));
                    NewLines();
                } while (Take(","));
            }
        }

        public IReadOnlyList<(string Name, SourceLocation Location)> HeaderBases()
        {
            var position = _position;
            try
            {
                if (Current.Text == "(") SkipBalanced("(", ")");
                var result = new List<(string Name, SourceLocation Location)>();
                if (Take(":"))
                    do
                    {
                        var token = HeaderTypeToken();
                        result.Add((token.Text, token.Location));
                        if(Current.Text=="(")SkipBalanced("(",")");
                    } while (Take(","));
                return result;
            }
            finally { _position = position; }
        }

        private void CheckTypeArguments(int template, IReadOnlyList<ScalarType> arguments, SourceLocation at)
        {
            if (typeGenerics is null || !typeGenerics.TryGetValue(template, out var definition)
                || !definition.Parameters.Any(p => p.Constraints.Count > 0) && !arguments.Any(a => IsStruct(a) && StructOf(a).IsInterface)) return;
            var use = new TypeUse(template, arguments.ToArray(), at, (_typeArgs is not null || _memberArgs is not null) && !_checkOnly);
            if (typeConstraintsReady && !hierarchyBusy && genericBindingDepth == 0) { ValidateTypeUse(use); return; }
            var key = template + ":" + string.Join(",", arguments.Select(a => (int)a)) + ":" + use.DeclarationChecked;
            if (typeUseKeys!.Add(key)) typeUses!.Add(use);
        }

        public void ValidateTypeUse(TypeUse use)
        {
            var key = use.Template + ":" + string.Join(",", use.Arguments.Select(t => (int)t));
            if (!validatingTypeUses!.Add(key)) return;
            try
            {
            var definition = typeGenerics![use.Template];
            var map = definition.Parameters.Select((p, i) => (p.Name, Type: use.Arguments[i])).ToDictionary(p => p.Name, p => p.Type);
            var identities = definition.Parameters.Select((p, i) => (Type: (ScalarType)(StructTypeBase + p.Placeholder), Argument: use.Arguments[i])).ToDictionary(p => p.Type, p => p.Argument);
            for (var i = 0; i < definition.Parameters.Count; i++)
            {
                var parameter = definition.Parameters[i];
                if (!ValidStaticInterfaceArgument(use.Arguments[i])) Fail("AI3107", "An interface with an unresolved static contract cannot be used as a type argument.", use.At);
                if (parameter.Placeholder < 0) Fail("AI3106", "Generic type declaration not bound.", use.At);
                if (!Satisfies(use.Arguments[i], ReboundConstraint(_structs[parameter.Placeholder], map, use.At, identities), use.DeclarationChecked))
                    Fail("AI3107", $"The type '{Show(use.Arguments[i])}' cannot be used as type parameter '{parameter.Name}' of '{definition.Name}': it does not satisfy the constraints.", use.At);
            }
            }
            finally { validatingTypeUses.Remove(key); }
        }
    }
}
