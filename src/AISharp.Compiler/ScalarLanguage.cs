using System.Globalization;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public enum ScalarType { Int, Bool, Unit, Long, UInt, ULong, Short, UShort, Byte, SByte, F32, F64, Char, String, Decimal, NInt, NUInt }
public sealed record ScalarExpression(string Operation, ScalarType Type, SourceLocation Location,
    string? Value = null, ScalarExpression? Left = null, ScalarExpression? Right = null)
{
    /// <summary>Extended front end only: arithmetic/conversion wraps instead of throwing.</summary>
    public bool Wraps { get; init; }
    /// <summary>Extended front end only: call arguments in source order.</summary>
    public IReadOnlyList<ScalarExpression>? Arguments { get; init; }
    public string? AccessorSetter { get; init; }
    public ScalarParameterMode ReferenceMode { get; init; }
    public IReadOnlyList<int>? ReferenceExcludedArguments { get; init; }
    public ScalarRuntimeTypeShape? RuntimeTypeShape { get; init; }
    public ScalarRuntimeTypeProof? RuntimeTypeProof { get; init; }
}
public sealed record ScalarStatement(string Kind, SourceLocation Location,
    ScalarExpression? Expression = null, string? Name = null, ScalarType Type = ScalarType.Int,
    IReadOnlyList<ScalarStatement>? Body = null, IReadOnlyList<ScalarStatement>? Alternative = null,
    ScalarStatement? Initializer = null, ScalarExpression? Increment = null)
{
    public bool Mutable { get; init; }
    /// <summary>Extended front end only: compile-time constant local.</summary>
    public bool Const { get; init; }
    public ScalarParameterMode ReferenceMode { get; init; }
    public bool Scoped { get; init; }
}
public sealed record ScalarContract(ContractKind Kind, ScalarExpression Expression, string Text, SourceLocation Location);
public enum ScalarParameterMode { Value, Ref, Out, In, RefReadonly }
public sealed record ScalarParameter(string Name, ScalarType Type)
{
    public ScalarParameterMode Mode { get; init; }
    public bool Scoped { get; init; }
}
public sealed record ScalarFunction(string Name, IReadOnlyList<ScalarParameter> Parameters, ScalarType ReturnType,
    IReadOnlyList<ScalarStatement> Body, IReadOnlyList<ScalarContract> Contracts, SourceLocation Location)
{
    public IReadOnlyList<string> ParameterNames => Parameters.Select(p => p.Name).ToArray();
    public ScalarParameterMode ReturnMode { get; init; }
}
public sealed record ScalarSource(string Text, SourceLocation Location);

/// <summary>Closed scalar front end. Every accepted expression is bound and typed before lowering.</summary>
public static partial class ScalarLanguage
{
    [ThreadStatic] private static bool invalidExtendedDeclaration;
    public const string LegacyProfile = "legacy-0.1";
    public const string StrictProfile = "native-strict-0.2";
    public static bool IsProfile(string profile) => profile is LegacyProfile or StrictProfile;
    public static IReadOnlyList<string> StrictSourceCollectionNames { get; } = Array.AsReadOnly(new[]
    {
        "Uses", "Policies", "Capabilities", "Resources", "Effects", "EffectDeclarations", "Types", "Schemas",
        "Tools", "Macros", "Generators", "Functions", "Actors", "Tests", "Observability"
    });
    internal static bool UsesNewSyntax(string body)
    {
        // Lex once: comments and complete string tokens cannot introduce new statements.
        // Unlike a multiline whitespace regex, blank lines are visited only once.
        var lexer = new Lexer(body);
        var tokens = lexer.Lex();
        if (lexer.Diagnostics.Count != 0) return false;
        var lineStart = true;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.NewLine) { lineStart = true; continue; }
            if (lineStart && token.Kind == TokenKind.Identifier)
            {
                var next = tokens[i + 1];
                if (token.Text is "break" or "continue"
                    || token.Text == "var" && next.Kind == TokenKind.Identifier
                    || token.Text is "if" or "while" or "for" && next.Kind == TokenKind.LParen)
                    return true;
            }
            lineStart = false;
        }
        return false;
    }

    internal static ParseResult Bind(ParseResult parsed, string profile)
    {
        if(invalidExtendedDeclaration)return new(parsed.Document with{Profile=profile,ScalarFunctions=[],ScalarValidationFailed=true},parsed.Diagnostics);
        var diagnostics = parsed.Diagnostics.ToList();
        var functions = new List<ScalarFunction>();
        var doc = parsed.Document;
        if (profile == StrictProfile && (ActiveFeatures & ScalarFeatures.EscapedIdentifiers) != 0)
        {
            foreach(var function in doc.Functions)
                if(!IsEscapedIdentifier(function.Name) && ReservedTypeParameterNames.Contains(function.Name))
                    diagnostics.Add(new("AI3106",DiagnosticSeverity.Error,$"Invalid function name '{function.Name}'.",function.Location));
            doc = doc with { Functions = doc.Functions.Select(f => f with { Name = IdentifierName(f.Name) }).ToArray() };
        }
        else if (profile == StrictProfile && ActiveFeatures == ScalarFeatures.None)
        {
            // Validate source spelling before canonicalizing names for HIR/MIR/lowering.
            // A Unicode-escaped or @-escaped identifier is not a literal keyword token.
            foreach (var function in doc.Functions)
                if (IdentifierSpelling.Valid(function.Name) && function.Name[0] != '@'
                    && IdentifierSpelling.Name(function.Name) == function.Name && ReservedTypeParameterNames.Contains(function.Name))
                    diagnostics.Add(new("AI3106", DiagnosticSeverity.Error, $"Invalid function name '{function.Name}'.", function.Location));
            doc = doc with { Functions = doc.Functions.Select(f => f with
                { Name = IdentifierSpelling.Valid(f.Name) ? IdentifierSpelling.Name(f.Name) : f.Name }).ToArray() };
        }
        if (!IsProfile(profile))
            return new(doc with { Profile = profile, ScalarValidationFailed = true },
                [.. diagnostics, new("AI3109", DiagnosticSeverity.Error, $"Unknown language profile '{profile}'.", new(1, 1, 0))]);
        if (profile == StrictProfile)
        {
            void Reject(SourceLocation location, string code = "AI3106") => diagnostics.Add(new(code, DiagnosticSeverity.Error,
                code == "AI3110" ? "void/unit fields are forbidden in the strict profile."
                    : "This strict scalar capability supports only module, intent and scalar functions.", location));
            foreach (var declaration in doc.Uses) Reject(declaration.Location);
            foreach (var declaration in doc.Policies) Reject(declaration.Location);
            foreach (var declaration in doc.Capabilities) Reject(declaration.Location);
            foreach (var declaration in doc.Resources) Reject(declaration.Location);
            foreach (var declaration in doc.EffectDeclarations) Reject(declaration.Location);
            if (doc.Effects.Count > 0 && doc.EffectDeclarations.Count == 0) Reject(doc.ModuleDeclaration?.Location ?? new(1, 1, 0));
            foreach (var type in doc.Types)
                Reject(type.Location, type.Fields.Any(f => f.Type is "void" or "unit") ? "AI3110" : "AI3106");
            foreach (var declaration in doc.Schemas)
                Reject(declaration.Location, declaration.Fields.Any(f => f.Type is "void" or "unit") ? "AI3110" : "AI3106");
            foreach (var declaration in doc.Tools) Reject(declaration.Location);
            foreach (var declaration in doc.Macros) Reject(declaration.Location);
            foreach (var declaration in doc.Generators) Reject(declaration.Location);
            foreach (var declaration in doc.Actors)
                Reject(declaration.Location, declaration.StateMembers.Any(f => f.Type is "void" or "unit") ? "AI3110" : "AI3106");
            foreach (var declaration in doc.Tests) Reject(declaration.Location);
            foreach (var declaration in doc.Observability) Reject(declaration.Location);
        }
        // The managed narrow-integer capability reuses the native typed numeric binder. Select it
        // from lexical type spellings, never comments, strings or escaped identifiers.
        var managedNumeric = profile == StrictProfile && ActiveFeatures == ScalarFeatures.None
            && doc.Functions.Any(f => f.ReturnType is "byte" or "u8" or "sbyte" or "i8" or "short" or "i16"
                || f.Parameters.Any(p => p.Type is "byte" or "u8" or "sbyte" or "i8" or "short" or "i16")
                || f.Body.ScalarSource is { } body && new Lexer(body.Text).Lex().Any(t => t.Kind == TokenKind.Identifier && t.Text is "byte" or "u8" or "sbyte" or "i8" or "short" or "i16"));
        var bindingFeatures = managedNumeric ? ScalarFeatures.Types | ScalarFeatures.Control | ScalarFeatures.Checked
            | ScalarFeatures.Bits | ScalarFeatures.Functions | ScalarFeatures.Floats | ScalarFeatures.Chars | ScalarFeatures.Decimals | ScalarFeatures.EscapedIdentifiers | ScalarFeatures.UnicodeIdentifiers : ActiveFeatures;
        var savedFeatures = activeFeatures;
        if (managedNumeric) activeFeatures = bindingFeatures;
        try
        {
        foreach (var function in doc.Functions)
        {
            if (function.Body.ScalarSource is null)
            {
                if (profile == StrictProfile)
                    diagnostics.Add(new("AI3106", DiagnosticSeverity.Error, "Strict function body was not captured for scalar validation.", function.Location));
                continue;
            }
            try { functions.Add(profile == StrictProfile && bindingFeatures != ScalarFeatures.None ? new ExtendedBinder(function, bindingFeatures, doc.Functions).Bind() : new Binder(function, profile == StrictProfile).Bind()); }
            catch (ScalarError error) { diagnostics.Add(error.Diagnostic); }
        }
        }
        finally { activeFeatures = savedFeatures; }
        return new(doc with { ScalarFunctions = functions, Profile = profile,
            ScalarValidationFailed = (profile == StrictProfile || doc.Functions.Any(f => f.Body.ScalarSource is not null))
                && diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) }, diagnostics);
    }

    private sealed class ScalarError(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }
    private sealed record Lexeme(string Text, SourceLocation Location) { public string Name => IdentifierName(Text); }
    private sealed record Local(string EmittedName, ScalarType Type, bool Mutable);
    private sealed class Binder
    {
        private readonly FunctionDeclaration _function;
        private readonly bool _strict;
        private List<Lexeme> _tokens;
        private int _position;
        private int _nextId;
        private int _loops;
        private bool _contract;
        private readonly List<Dictionary<string, Local>> _scopes = [new(StringComparer.Ordinal)];
        private HashSet<string> _assigned = [];
        private ScalarType _returnType;
        private Lexeme Current => _tokens[_position];
        public Binder(FunctionDeclaration function, bool strict)
        {
            _function = function;
            _strict = strict;
            var body = function.Body.ScalarSource!;
            _tokens = Lex(body.Text, body.Location);
        }
        private static ScalarError Error(string code, string message, SourceLocation location) =>
            new(new(code, DiagnosticSeverity.Error, message, location));
        private void Fail(string code, string message, SourceLocation? location = null) =>
            throw Error(code, message, location ?? Current.Location);
        private bool Take(string text)
        {
            if (Current.Text != text) return false;
            if (_position < _tokens.Count - 1) _position++;
            return true;
        }
        private Lexeme Next() { var token = Current; if (_position < _tokens.Count - 1) _position++; return token; }
        private void Expect(string text)
        {
            if (!Take(text)) Fail("AI3106", $"Expected '{text}'.");
        }
        private void NewLines() { while (Take("\n")) { } }
        private void EndStatement()
        {
            if (Take(";")) { NewLines(); return; }
            if (Current.Text is "\n" or "}" or "") { NewLines(); return; }
            Fail("AI3106", "Expected a statement boundary; one declarator is supported.");
        }
        private ScalarType Type(string text, SourceLocation location, bool allowUnit = false)
        {
            if (text is "int" or "i32") return ScalarType.Int;
            if (text == "bool") return ScalarType.Bool;
            if (allowUnit && text is "unit" or "void") return ScalarType.Unit;
            Fail(text is "unit" or "void" ? "AI3110" : "AI3106", $"Unsupported scalar type '{text}'.", location);
            return ScalarType.Unit;
        }
        public ScalarFunction Bind()
        {
            if (!IdentifierSpelling.Valid(_function.Name))
                Fail("AI3106", "Unsupported scalar function identifier.", _function.Location);
            if (_function.IsAsync || _function.Effects.Count != 0)
                Fail("AI3106", "Async and effectful functions are outside the scalar capability.", _function.Location);
            _returnType = Type(_function.ReturnType, _function.Location, true);
            var parameters = new List<ScalarParameter>();
            foreach (var parameter in _function.Parameters)
            {
                if (parameter.Ownership != ParameterOwnership.Unspecified)
                    Fail("AI3106", "Ownership parameters are outside the scalar capability.", _function.Location);
                var local = Declare(parameter.Name, Type(parameter.Type, _function.Location), true, parameter.NameLocation ?? _function.Location);
                parameters.Add(new(local.EmittedName, local.Type));
                _assigned.Add(local.EmittedName);
            }
            var contractTokens = _tokens;
            var contracts = new List<ScalarContract>();
            foreach (var contract in _function.Contracts)
            {
                _tokens = Lex(contract.Expression, contract.ExpressionLocation ?? contract.Location);
                _position = 0;
                _contract = true;
                _scopes.Add(new(StringComparer.Ordinal));
                if (contract.Kind != ContractKind.Requires && _returnType != ScalarType.Unit)
                {
                    _scopes[^1]["result"] = new("__scalar_result", _returnType, false);
                    _assigned.Add("__scalar_result");
                }
                var expression = Expression();
                NewLines();
                if (Current.Text != "") Fail("AI3100", "Unsupported contract expression.");
                Require(expression, ScalarType.Bool, "AI3040");
                contracts.Add(new(contract.Kind, expression, contract.Expression, contract.Location));
                _scopes.RemoveAt(_scopes.Count - 1);
                _assigned.Remove("__scalar_result");
            }
            _tokens = contractTokens; _position = 0; _contract = false;
            var (body, returns) = Block(false);
            if (Current.Text != "") Fail("AI3106", "Unexpected closing brace.");
            if (_returnType != ScalarType.Unit && !returns)
                Fail("AI3108", "A reachable path has no return value.", _function.Location);
            return new(_function.Name, parameters, _returnType, body, contracts, _function.Location);
        }
        private Local Declare(string name, ScalarType type, bool mutable, SourceLocation location)
        {
            if (!SourceIdentifier(name))
                Fail("AI3106", $"Invalid local name '{name}'.", location);
            name = IdentifierSpelling.Name(name);
            if (_scopes[^1].ContainsKey(name)) Fail("AI3106", $"Duplicate local '{name}'.", location);
            var local = new Local($"__scalar_local_{_nextId++}", type, mutable);
            _scopes[^1].Add(name, local);
            return local;
        }
        private static bool IsReserved(string name) => name is "var" or "let" or "if" or "else" or "while"
            or "for" or "break" or "continue" or "return" or "true" or "false" or "int" or "bool";
        private static bool SourceIdentifier(string text) => IdentifierSpelling.Valid(text)
            && (text[0] == '@' || IdentifierSpelling.Name(text) != text || !IsReserved(text));
        private Local Lookup(Lexeme token)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(IdentifierSpelling.Name(token.Text), out var local)) return local;
            Fail("AI3103", $"Unresolved or out-of-scope local '{token.Text}'.", token.Location);
            return null!;
        }
        private (IReadOnlyList<ScalarStatement> Body, bool Returns) Block(bool braces = true)
        {
            if (braces) { NewLines(); Expect("{"); _scopes.Add(new(StringComparer.Ordinal)); }
            NewLines();
            var statements = new List<ScalarStatement>();
            var returns = false;
            while (Current.Text is not "" and not "}")
            {
                var (statement, terminates) = Statement();
                statements.Add(statement);
                returns |= terminates;
                NewLines();
            }
            if (braces)
            {
                Expect("}");
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
            }
            return (statements, returns);
        }
        private (ScalarStatement Statement, bool Returns) Statement()
        {
            var token = Current;
            if (token.Text is "foreach" or "do" or "switch" or "goto" or "try" or "throw" or "using" or "match"
                or "async" or "await" or "const" or "taskgroup" or "spawn" or "lock" or "unchecked")
                Fail("AI3106", $"Statement '{token.Text}' is outside the scalar capability.", token.Location);
            if (token.Text is "let" or "var")
            {
                var declaration = Declaration(); EndStatement(); return (declaration, false);
            }
            if (Take("return"))
            {
                ScalarExpression? expression = Current.Text is "\n" or ";" or "}" or "" ? null : Expression();
                if (expression is null ? _returnType != ScalarType.Unit : expression.Type != _returnType)
                    Fail("AI3020", "Return value does not match the declared type.", token.Location);
                EndStatement(); return (new("return", token.Location, expression), true);
            }
            if (Take("if"))
            {
                var condition = Condition(); var before = new HashSet<string>(_assigned);
                var yes = Block(); var yesAssigned = new HashSet<string>(_assigned);
                _assigned = new(before); NewLines();
                (IReadOnlyList<ScalarStatement> Body, bool Returns) no = ([], false);
                if (Take("else"))
                {
                    NewLines();
                    if (Current.Text == "if") { var nested = Statement(); no = ([nested.Statement], nested.Returns); }
                    else no = Block();
                }
                if (BooleanConstant(condition) == true) _assigned = yesAssigned;
                else if (BooleanConstant(condition) == false) { /* only the else path is reachable */ }
                else if (yes.Returns && !no.Returns) { /* only the else path reaches the next statement */ }
                else if (no.Returns && !yes.Returns) _assigned = yesAssigned;
                else _assigned.IntersectWith(yesAssigned);
                var terminates = BooleanConstant(condition) switch { true => yes.Returns, false => no.Returns, null => yes.Returns && no.Returns };
                return (new("if", token.Location, condition, Body: yes.Body, Alternative: no.Body), terminates);
            }
            if (Take("while"))
            {
                var condition = Condition(); var before = new HashSet<string>(_assigned);
                _loops++; var body = Block(); _loops--; _assigned = before;
                return (new("while", token.Location, condition, Body: body.Body), BooleanConstant(condition) == true && !BreaksCurrentLoop(body.Body));
            }
            if (Take("for"))
            {
                Expect("("); _scopes.Add(new(StringComparer.Ordinal));
                if (Current.Text is not "var" and not "let") Fail("AI3106", "For requires a local initializer.");
                var initializer = Declaration();
                if (initializer.Expression is null) Fail("AI3106", "For initializer requires a value.");
                Expect(";"); var condition = Expression(); Require(condition, ScalarType.Bool, "AI3101"); Expect(";");
                var before = new HashSet<string>(_assigned);
                var increment = Expression();
                if (increment.Operation is not "=" and not "+=" and not "-=" and not "&=" and not "|=" and not "^=" and not "pre++" and not "pre--" and not "post++" and not "post--")
                    Fail("AI3106", "For increment requires an update expression.", increment.Location);
                Expect(")"); _assigned = new(before);
                _loops++; var body = Block(); _loops--; _assigned = before;
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
                return (new("for", token.Location, condition, Body: body.Body, Initializer: initializer, Increment: increment), BooleanConstant(condition) == true && !BreaksCurrentLoop(body.Body));
            }
            if (token.Text is "break" or "continue")
            {
                Next(); if (_loops == 0) Fail("AI3104", $"{token.Text} requires a loop.", token.Location);
                EndStatement(); return (new(token.Text, token.Location), true);
            }
            if (Current.Text == "{") { var block = Block(); return (new("block", token.Location, Body: block.Body), block.Returns); }
            var value = Expression();
            if (value.Operation is not "=" and not "+=" and not "-=" and not "&=" and not "|=" and not "^=" and not "pre++" and not "pre--" and not "post++" and not "post--")
                Fail("AI3106", "Only assignment/update expressions may be statements.", value.Location);
            EndStatement(); return (new("expression", token.Location, value), false);
        }
        private ScalarStatement Declaration()
        {
            var keyword = Next(); var name = Next(); ScalarType? annotation = null;
            if (Take(":")) { var type = Next(); annotation = Type(type.Text, type.Location); }
            var initializer = Take("=") ? Expression() : null;
            if (initializer is null && (annotation is null || keyword.Text == "let"))
                Fail("AI3106", "Inferred var and let require an initializer.", keyword.Location);
            var resolved = annotation ?? initializer!.Type;
            if (initializer is not null) Require(initializer, resolved, "AI3107");
            var local = Declare(name.Text, resolved, keyword.Text == "var" || !_strict, name.Location);
            if (initializer is not null) _assigned.Add(local.EmittedName);
            return new ScalarStatement("local", keyword.Location, initializer, local.EmittedName, resolved) { Mutable = local.Mutable };
        }
        private static bool? BooleanConstant(ScalarExpression expression) => expression.Operation switch
        {
            "literal" when expression.Type == ScalarType.Bool => expression.Value == "true",
            "unary!" => BooleanConstant(expression.Left!) is bool value ? !value : null,
            "&&" => BooleanConstant(expression.Left!) is bool left && BooleanConstant(expression.Right!) is bool right ? left && right : null,
            "||" => BooleanConstant(expression.Left!) is bool left && BooleanConstant(expression.Right!) is bool right ? left || right : null,
            "&" or "|" or "^" or "==" or "!=" when BooleanConstant(expression.Left!) is bool left && BooleanConstant(expression.Right!) is bool right =>
                expression.Operation switch { "&" => left & right, "|" => left | right, "^" => left ^ right, "==" => left == right, _ => left != right },
            _ => null
        };
        private static bool BreaksCurrentLoop(IReadOnlyList<ScalarStatement> statements)
        {
            foreach (var statement in statements)
            {
                if (statement.Kind == "break") return true;
                if (statement.Kind is "return" or "continue") return false;
                if (statement.Kind == "block" && BreaksCurrentLoop(statement.Body!)) return true;
                if (statement.Kind == "if")
                {
                    var constant = BooleanConstant(statement.Expression!);
                    if (constant != false && BreaksCurrentLoop(statement.Body!)) return true;
                    if (constant != true && BreaksCurrentLoop(statement.Alternative!)) return true;
                }
                // A nested loop owns its breaks and cannot terminate this loop.
            }
            return false;
        }
        private ScalarExpression Condition()
        {
            Expect("("); var expression = Expression(); Expect(")"); Require(expression, ScalarType.Bool, "AI3101"); return expression;
        }
        private void Require(ScalarExpression expression, ScalarType type, string code = "AI3107")
        {
            if (expression.Type != type) Fail(code, $"Expected {type}, found {expression.Type}.", expression.Location);
        }
        private static int Precedence(string op) => op switch
        {
            "=" or "+=" or "-=" or "&=" or "|=" or "^=" => 1, "||" => 2, "&&" => 3,
            "|" => 4, "^" => 5, "&" => 6, "==" or "!=" => 7,
            "<" or "<=" or ">" or ">=" => 8, "+" or "-" => 9, "*" or "/" or "%" => 10, _ => 0
        };
        private ScalarExpression Expression(int minimum = 1)
        {
            var left = Unary();
            while (Precedence(Current.Text) >= minimum)
            {
                var op = Next(); var precedence = Precedence(op.Text);
                var assignment = precedence == 1;
                var beforeRight = new HashSet<string>(_assigned);
                if (!assignment || op.Text != "=") Read(left);
                var right = Expression(assignment ? precedence : precedence + 1);
                Read(right);
                if (assignment)
                {
                    Write(left);
                    Require(right, left.Type);
                    if (op.Text != "=") Require(left, op.Text is "&=" or "|=" or "^=" ? ScalarType.Bool : ScalarType.Int);
                    _assigned.Add(left.Value!);
                    left = new(op.Text, left.Type, op.Location, Left: left, Right: right);
                }
                else
                {
                    ScalarType result;
                    if (op.Text is "&&" or "||")
                    {
                        Require(left, ScalarType.Bool); Require(right, ScalarType.Bool);
                        _assigned.IntersectWith(beforeRight); result = ScalarType.Bool;
                    }
                    else if (op.Text is "&" or "|" or "^")
                    { Require(left, ScalarType.Bool); Require(right, ScalarType.Bool); result = ScalarType.Bool; }
                    else if (op.Text is "==" or "!=") { Require(right, left.Type); result = ScalarType.Bool; }
                    else
                    {
                        Require(left, ScalarType.Int); Require(right, ScalarType.Int);
                        result = precedence == 8 ? ScalarType.Bool : ScalarType.Int;
                    }
                    left = new(op.Text, result, op.Location, Left: left, Right: right);
                }
            }
            // The left of plain '=' may be unassigned; all other expression results must be readable.
            if (minimum == 1 && Current.Text != "=") Read(left);
            return left;
        }
        private ScalarExpression Unary()
        {
            var token = Current;
            if (token.Text is "+" or "-" or "!" or "++" or "--")
            {
                Next();
                if (token.Text == "-" && Current.Text == "2147483648")
                { Next(); return new("literal", ScalarType.Int, token.Location, "-2147483648"); }
                var operand = Unary(); Read(operand);
                Require(operand, token.Text == "!" ? ScalarType.Bool : ScalarType.Int);
                if (token.Text is "++" or "--") Write(operand);
                return new(token.Text is "++" or "--" ? "pre"+token.Text : "unary"+token.Text,
                    operand.Type, token.Location, Left: operand);
            }
            ScalarExpression expression;
            if (Take("(")) { expression = Expression(); Expect(")"); }
            else
            {
                Next();
                if (token.Text is "true" or "false") expression = new("literal", ScalarType.Bool, token.Location, token.Text);
                else if (token.Text.Length > 0 && char.IsAsciiDigit(token.Text[0]))
                {
                    if (!int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        Fail("AI3107", "Integer literal is outside Int32 or is not decimal.", token.Location);
                    expression = new("literal", ScalarType.Int, token.Location, token.Text);
                }
                else if (SourceIdentifier(token.Text))
                {
                    if (Current.Text is "(" or "." or "[") Fail("AI3100", "Calls and member/index access are outside the scalar capability.", token.Location);
                    var local = Lookup(token); expression = new("local", local.Type, token.Location, local.EmittedName);
                }
                else { Fail("AI3100", $"Unsupported scalar expression '{token.Text}'.", token.Location); return null!; }
            }
            if (Current.Text is "++" or "--")
            {
                var op = Next(); Read(expression); Write(expression); Require(expression, ScalarType.Int);
                expression = new("post"+op.Text, expression.Type, op.Location, Left: expression);
            }
            if (Current.Text is "." or "[" or "(" or "?")
                Fail("AI3100", "Unsupported scalar expression.");
            return expression;
        }
        private void Read(ScalarExpression expression)
        {
            if (expression.Operation == "local" && !_assigned.Contains(expression.Value!))
                Fail("AI3102", "Local is read before assignment on every reaching path.", expression.Location);
        }
        private void Write(ScalarExpression expression)
        {
            if (_contract) Fail("AI3100", "Contracts cannot mutate scalar locals.", expression.Location);
            if (expression.Operation != "local") Fail("AI3107", "Assignment requires local storage.", expression.Location);
            var local = _scopes.SelectMany(s => s.Values).First(l => l.EmittedName == expression.Value);
            if (!local.Mutable) Fail("AI3105", "Cannot write an immutable let.", expression.Location);
        }
        private static List<Lexeme> Lex(string source, SourceLocation start)
        {
            var tokens = new List<Lexeme>();
            int i = 0, line = start.Line, column = start.Column;
            while (i < source.Length)
            {
                var c = source[i];
                if (c is ' ' or '\t' or '\r') { i++; column++; continue; }
                if (c == '/' && i+1 < source.Length && source[i+1] == '/')
                { while (i < source.Length && source[i] != '\n') { i++; column++; } continue; }
                var location = new SourceLocation(line, column, start.Offset+i);
                if (c == '\n') { tokens.Add(new("\n",location)); i++; line++; column=1; continue; }
                var begin = i;
                if (IdentifierSpelling.TryRead(source, i, out var identifierEnd)) i = identifierEnd;
                else if (char.IsAsciiDigit(c))
                    while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                else if (i+1 < source.Length && source.Substring(i,2) is "++" or "--" or "+=" or "-=" or "&=" or "|=" or "^=" or "==" or "!=" or "<=" or ">=" or "&&" or "||") i+=2;
                else i++;
                tokens.Add(new(source[begin..i], location)); column += i-begin;
            }
            tokens.Add(new("",new(line,column,start.Offset+i))); return tokens;
        }
    }
}
