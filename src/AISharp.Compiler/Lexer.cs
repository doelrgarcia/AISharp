namespace AISharp.Compiler;

internal enum TokenKind
{
    EndOfFile,
    Identifier,
    String,
    Intent,
    Policy,
    Capability,
    Resource,
    Effect,
    Type,
    Schema,
    Tool,
    Macro,
    Generator,
    Fn,
    Requires,
    Ensures,
    Invariant,
    Test,
    Observe,
    Effects,
    Module,
    Use,
    Async,
    Actor,
    State,
    Match,
    FatArrow,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    Colon,
    Comma,
    Pipe,
    Equals,
    Arrow,
    NewLine,
    Other
}

internal sealed record Token(TokenKind Kind, string Text, SourceLocation Location);

internal sealed class Lexer
{
    private readonly string _source;
    private readonly List<Diagnostic> _diagnostics = [];
    private int _index;
    private int _line = 1;
    private int _column = 1;

    public Lexer(string source)
    {
        _source = source;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public List<Token> Lex()
    {
        var tokens = new List<Token>();

        while (!IsAtEnd)
        {
            var location = CurrentLocation;
            var current = Current;

            if (current is ' ' or '\t')
            {
                Advance();
                continue;
            }

            if (current == '/' && Peek() == '/')
            {
                // Line comment: skip everything to end of line (no token produced)
                while (!IsAtEnd && Current != '\r' && Current != '\n')
                {
                    Advance();
                }
                continue;
            }

            if (current == '\r' || current == '\n')
            {
                ConsumeNewLine();
                tokens.Add(new Token(TokenKind.NewLine, "\n", location));
                continue;
            }

            if (IdentifierSpelling.TryRead(_source, _index, out var identifierEnd, allowLegacyHash: true))
            {
                tokens.Add(ReadIdentifier(identifierEnd));
                continue;
            }

            if (current == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            switch (current)
            {
                case '(':
                    tokens.Add(Single(TokenKind.LParen));
                    break;
                case ')':
                    tokens.Add(Single(TokenKind.RParen));
                    break;
                case '{':
                    tokens.Add(Single(TokenKind.LBrace));
                    break;
                case '}':
                    tokens.Add(Single(TokenKind.RBrace));
                    break;
                case '[':
                    tokens.Add(Single(TokenKind.LBracket));
                    break;
                case ']':
                    tokens.Add(Single(TokenKind.RBracket));
                    break;
                case ':':
                    tokens.Add(Single(TokenKind.Colon));
                    break;
                case ',':
                    tokens.Add(Single(TokenKind.Comma));
                    break;
                case '|':
                    tokens.Add(Single(TokenKind.Pipe));
                    break;
                case '=' when Peek() == '>':
                    Advance();
                    Advance();
                    tokens.Add(new Token(TokenKind.FatArrow, "=>", location));
                    break;
                case '=':
                    tokens.Add(Single(TokenKind.Equals));
                    break;
                case '-' when Peek() == '>':
                    Advance();
                    Advance();
                    tokens.Add(new Token(TokenKind.Arrow, "->", location));
                    break;
                default:
                    Advance();
                    tokens.Add(new Token(TokenKind.Other, current.ToString(), location));
                    break;
            }
        }

        tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, CurrentLocation));
        return tokens;
    }

    private bool IsAtEnd => _index >= _source.Length;

    private char Current => IsAtEnd ? '\0' : _source[_index];

    private SourceLocation CurrentLocation => new(_line, _column, _index);

    private char Peek() => _index + 1 >= _source.Length ? '\0' : _source[_index + 1];

    private Token Single(TokenKind kind)
    {
        var location = CurrentLocation;
        var text = Current.ToString();
        Advance();
        return new Token(kind, text, location);
    }

    private Token ReadIdentifier(int end)
    {
        var location = CurrentLocation;
        var start = _index;

        while (_index < end)
        {
            Advance();
        }

        var text = _source[start.._index];
        var kind = text switch
        {
            "intent" => TokenKind.Intent,
            "policy" => TokenKind.Policy,
            "capability" => TokenKind.Capability,
            "resource" => TokenKind.Resource,
            "effect" => TokenKind.Effect,
            "type" => TokenKind.Type,
            "schema" => TokenKind.Schema,
            "tool" => TokenKind.Tool,
            "macro" => TokenKind.Macro,
            "generator" => TokenKind.Generator,
            "fn" => TokenKind.Fn,
            "requires" => TokenKind.Requires,
            "ensures" => TokenKind.Ensures,
            "invariant" => TokenKind.Invariant,
            "test" => TokenKind.Test,
            "observe" => TokenKind.Observe,
            "effects" => TokenKind.Effects,
            "module" => TokenKind.Module,
            "use" => TokenKind.Use,
            "async" => TokenKind.Async,
            "actor" => TokenKind.Actor,
            "state" => TokenKind.State,
            "match" => TokenKind.Match,
            _ => TokenKind.Identifier
        };

        return new Token(kind, text, location);
    }

    private Token ReadString()
    {
        var location = CurrentLocation;
        Advance();
        var start = _index;

        while (!IsAtEnd && Current != '"' && Current != '\r' && Current != '\n')
        {
            if (Current == '\\' && Peek() != '\0')
            {
                Advance();
            }

            Advance();
        }

        var text = _source[start.._index];
        if (IsAtEnd || Current is '\r' or '\n')
        {
            _diagnostics.Add(new Diagnostic("AI0001", DiagnosticSeverity.Error, "Unterminated string literal.", location));
            return new Token(TokenKind.String, text, location);
        }

        Advance();
        return new Token(TokenKind.String, text.Replace("\\\"", "\"", StringComparison.Ordinal), location);
    }

    private void ConsumeNewLine()
    {
        if (Current == '\r')
        {
            Advance();
            if (Current == '\n')
            {
                Advance();
            }
        }
        else
        {
            Advance();
        }
    }

    private void Advance()
    {
        if (IsAtEnd)
        {
            return;
        }

        if (_source[_index] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        _index++;
    }
}
