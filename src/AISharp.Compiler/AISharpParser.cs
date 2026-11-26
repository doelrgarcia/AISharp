namespace AISharp.Compiler;

public static class AISharpParser
{
    public static ParseResult Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var lexer = new Lexer(source);
        var tokens = lexer.Lex();
        var parser = new Parser(source, tokens, lexer.Diagnostics);
        return parser.ParseDocument();
    }

    // Explicit profile entry used by the native capability; the original legacy entry stays unchanged.
    public static ParseResult Parse(string source, string profile)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lexer = new Lexer(source);
        var tokens = lexer.Lex();
        var parser = new Parser(source, tokens, lexer.Diagnostics, profile);
        return ScalarLanguage.Bind(parser.ParseDocument(), profile);
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<Token> _tokens;
        private readonly List<Diagnostic> _diagnostics;
        private readonly string _source;
        private int _position;
        private readonly string _profile;

        private sealed record SourceBlock(string Text, int Offset);

        private sealed record BodyLine(string Text, int Offset);

        public Parser(string source, IReadOnlyList<Token> tokens, IReadOnlyList<Diagnostic> lexerDiagnostics, string profile = ScalarLanguage.LegacyProfile)
        {
            _source = source;
            _profile = profile;
            _tokens = tokens;
            _diagnostics = [.. lexerDiagnostics];
        }

        public ParseResult ParseDocument()
        {
            string? intent = null;
            string? module = null;
            ModuleDeclaration? moduleDeclaration = null;
            var uses = new List<UseDeclaration>();
            var policies = new List<PolicyDeclaration>();
            var capabilities = new List<CapabilityDeclaration>();
            var resources = new List<ResourceDeclaration>();
            var effects = new List<string>();
            var effectDeclarations = new List<EffectDeclaration>();
            var types = new List<TypeDeclaration>();
            var schemas = new List<SchemaDeclaration>();
            var tools = new List<ToolDeclaration>();
            var macros = new List<MacroDeclaration>();
            var generators = new List<GeneratorDeclaration>();
            var functions = new List<FunctionDeclaration>();
            var actors = new List<ActorDeclaration>();
            var tests = new List<TestDeclaration>();
            var observations = new List<ObservationDeclaration>();

            while (!At(TokenKind.EndOfFile))
            {
                SkipNewLines();

                if (At(TokenKind.EndOfFile))
                {
                    break;
                }

                switch (Current.Kind)
                {
                    case TokenKind.Module:
                        moduleDeclaration = ParseModule(module);
                        module = moduleDeclaration.Name;
                        break;
                    case TokenKind.Use:
                        uses.Add(ParseUse());
                        break;
                    case TokenKind.Intent:
                        intent = ParseIntent(intent);
                        break;
                    case TokenKind.Policy:
                        policies.Add(ParsePolicy());
                        break;
                    case TokenKind.Capability:
                        capabilities.Add(ParseCapability());
                        break;
                    case TokenKind.Resource:
                        resources.Add(ParseResource());
                        break;
                    case TokenKind.Effect:
                        var parsedEffects = ParseDocumentEffectList();
                        effectDeclarations.AddRange(parsedEffects);
                        effects.AddRange(parsedEffects.Select(effect => effect.Name));
                        break;
                    case TokenKind.Type:
                        types.Add(ParseType());
                        break;
                    case TokenKind.Schema:
                        schemas.Add(ParseSchema());
                        break;
                    case TokenKind.Tool:
                        tools.Add(ParseTool());
                        break;
                    case TokenKind.Macro:
                        macros.Add(ParseMacro());
                        break;
                    case TokenKind.Generator:
                        generators.Add(ParseGenerator());
                        break;
                    case TokenKind.Fn:
                        functions.Add(ParseFunction(isAsync: false));
                        break;
                    case TokenKind.Async:
                        functions.Add(ParseAsyncFunction());
                        break;
                    case TokenKind.Actor:
                        actors.Add(ParseActor());
                        break;
                    case TokenKind.Test:
                        tests.Add(ParseTest());
                        break;
                    case TokenKind.Observe:
                        observations.AddRange(ParseObserveBlock());
                        break;
                    default:
                        Report("AI1000", $"Unexpected token '{Current.Text}' at top level.", Current.Location);
                        Advance();
                        break;
                }
            }

            var document = new AISharpDocument(module, intent, uses, policies, capabilities, resources, effects.Distinct(StringComparer.Ordinal).ToArray(), types, schemas, tools, macros, generators, functions, actors, tests, observations)
            {
                ModuleDeclaration = moduleDeclaration,
                EffectDeclarations = effectDeclarations
            };
            ValidateDocument(document);
            return new ParseResult(document, _diagnostics);
        }

        private string? ParseIntent(string? existingIntent)
        {
            var location = Expect(TokenKind.Intent, "Expected 'intent'.").Location;
            if (existingIntent is not null)
            {
                Report("AI1001", "Only one intent declaration is allowed per file.", location);
            }

            var value = Expect(TokenKind.String, "Expected an intent string after 'intent'.");
            ConsumeLineRemainder();
            return value.Text;
        }

        private PolicyDeclaration ParsePolicy()
        {
            Expect(TokenKind.Policy, "Expected 'policy'.");
            var name = Expect(TokenKind.Identifier, "Expected policy name.");
            SkipNewLines();
            Expect(TokenKind.LBrace, "Expected '{' to start policy body.");

            var fields = new List<PolicyField>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RBrace))
            {
                SkipNewLines();
                if (At(TokenKind.RBrace))
                {
                    break;
                }

                var key = Expect(TokenKind.Identifier, "Expected policy field name.");
                Expect(TokenKind.Equals, $"Expected '=' after policy field '{key.Text}'.");
                var value = ReadUntilLineEnd();
                fields.Add(new PolicyField(key.Text, value));
            }

            Expect(TokenKind.RBrace, "Expected '}' to end policy body.");
            ConsumeLineRemainder();
            return new PolicyDeclaration(name.Text, fields, name.Location);
        }

        private CapabilityDeclaration ParseCapability()
        {
            Expect(TokenKind.Capability, "Expected 'capability'.");
            var name = Expect(TokenKind.Identifier, "Expected capability name.");
            ConsumeLineRemainder();
            return new CapabilityDeclaration(name.Text, name.Location);
        }

        private ResourceDeclaration ParseResource()
        {
            Expect(TokenKind.Resource, "Expected 'resource'.");
            var name = Expect(TokenKind.Identifier, "Expected resource name.");
            ConsumeLineRemainder();
            return new ResourceDeclaration(name.Text, name.Location);
        }

        private ModuleDeclaration ParseModule(string? existingModule)
        {
            var location = Expect(TokenKind.Module, "Expected 'module'.").Location;
            if (existingModule is not null)
            {
                Report("AI2018", "Only one module declaration is allowed per file.", location);
            }

            var path = ReadDottedName(out var pathLocation);
            return new ModuleDeclaration(path, pathLocation);
        }

        private UseDeclaration ParseUse()
        {
            Expect(TokenKind.Use, "Expected 'use'.");
            var path = ReadDottedName(out var location);
            return new UseDeclaration(path, location);
        }

        private string ReadDottedName(out SourceLocation location)
        {
            var sb = new System.Text.StringBuilder();
            var first = Expect(TokenKind.Identifier, "Expected a dotted path.");
            location = first.Location;
            sb.Append(first.Text);

            while (!At(TokenKind.EndOfFile) && !At(TokenKind.NewLine)
                   && At(TokenKind.Other) && Current.Text == ".")
            {
                Advance();
                sb.Append('.');
                if (At(TokenKind.Identifier))
                {
                    sb.Append(Current.Text);
                    Advance();
                }
                else
                {
                    Report("AI1011", "Expected identifier after '.' in dotted path.", Current.Location);
                    break;
                }
            }

            ConsumeLineRemainder();
            return sb.ToString();
        }

        private IReadOnlyList<EffectDeclaration> ParseDocumentEffectList()
        {
            Expect(TokenKind.Effect, "Expected 'effect'.");

            var effects = new List<EffectDeclaration>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.NewLine))
            {
                if (At(TokenKind.Identifier))
                {
                    var effect = Current;
                    if (!IsBuiltInEffect(effect.Text))
                    {
                        effects.Add(new EffectDeclaration(effect.Text, effect.Location));
                    }
                    else
                    {
                        Report("AI2027", $"Built-in effect '{effect.Text}' is first-class and must be used in a function signature, not declared at document scope.", effect.Location);
                    }

                    Advance();
                    if (At(TokenKind.Comma))
                    {
                        Advance();
                    }
                    continue;
                }

                Report("AI1002", "Expected effect name.", Current.Location);
                Advance();
            }

            ConsumeLineRemainder();
            return effects;
        }

        private IReadOnlyList<string> ParseEffectList(bool requireParentheses, bool allowBuiltInEffects)
        {
            Expect(TokenKind.Effect, "Expected 'effect'.");
            if (requireParentheses)
            {
                Expect(TokenKind.LParen, "Expected '(' after function effect.");
            }

            var effects = new List<string>();
            while (!At(TokenKind.EndOfFile)
                && !At(TokenKind.NewLine)
                && !(requireParentheses && At(TokenKind.RParen)))
            {
                if (At(TokenKind.Identifier))
                {
                    var effect = Current.Text;
                    if (!allowBuiltInEffects && IsBuiltInEffect(effect))
                    {
                        Report("AI2027", $"Built-in effect '{effect}' is first-class and must be used in a function signature, not declared at document scope.", Current.Location);
                    }
                    else
                    {
                        effects.Add(effect);
                    }

                    Advance();
                    if (At(TokenKind.Comma))
                    {
                        Advance();
                    }
                    continue;
                }

                Report("AI1002", "Expected effect name.", Current.Location);
                Advance();
            }

            if (requireParentheses)
            {
                Expect(TokenKind.RParen, "Expected ')' after function effect list.");
            }

            ConsumeLineRemainder();
            return effects;
        }

        private IReadOnlyList<string> ParseBracketedEffectList()
        {
            Expect(TokenKind.Effects, "Expected 'effects'.");
            Expect(TokenKind.LBracket, "Expected '[' after 'effects'.");

            var effects = new List<string>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RBracket) && !At(TokenKind.NewLine))
            {
                if (At(TokenKind.Identifier))
                {
                    effects.Add(Current.Text);
                    Advance();
                    if (At(TokenKind.Comma))
                    {
                        Advance();
                    }
                    continue;
                }

                Report("AI1002", "Expected effect name.", Current.Location);
                Advance();
            }

            Expect(TokenKind.RBracket, "Expected ']' after bracketed effect list.");
            ConsumeLineRemainder();
            return effects;
        }

        private TypeDeclaration ParseType()
        {
            Expect(TokenKind.Type, "Expected 'type'.");
            var name = Expect(TokenKind.Identifier, "Expected type name.");
            Expect(TokenKind.Equals, "Expected '=' in type declaration.");

            SkipNewLines();

            if (At(TokenKind.LBrace))
            {
                return ParseRecordTypeBody(name);
            }

            // Union type: Name = Case | Case | Case
            var cases = new List<string>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.NewLine))
            {
                if (At(TokenKind.Identifier))
                {
                    cases.Add(Current.Text);
                    Advance();
                    if (At(TokenKind.Pipe))
                    {
                        Advance();
                    }
                    continue;
                }

                Report("AI1003", "Expected union case name.", Current.Location);
                Advance();
            }

            ConsumeLineRemainder();
            if (cases.Count == 0)
            {
                Report("AI1004", $"Type '{name.Text}' must declare at least one union case.", name.Location);
            }

            return new TypeDeclaration(name.Text, TypeKind.Union, cases, [], name.Location);
        }

        private TypeDeclaration ParseRecordTypeBody(Token name)
        {
            Expect(TokenKind.LBrace, "Expected '{' to start record type body.");

            var fields = new List<ParameterDeclaration>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RBrace))
            {
                SkipNewLines();
                if (At(TokenKind.RBrace))
                {
                    break;
                }

                var fieldName = Expect(TokenKind.Identifier, "Expected record field name.");
                Expect(TokenKind.Colon, "Expected ':' after record field name.");
                var fieldType = ReadSchemaFieldType();
                fields.Add(new ParameterDeclaration(fieldName.Text, fieldType));

                if (At(TokenKind.Comma))
                {
                    Advance();
                }
            }

            Expect(TokenKind.RBrace, "Expected '}' to end record type body.");
            ConsumeLineRemainder();

            if (fields.Count == 0)
            {
                Report("AI1009", $"Record type '{name.Text}' must declare at least one field.", name.Location);
            }

            return new TypeDeclaration(name.Text, TypeKind.Record, [], fields, name.Location);
        }

        private SchemaDeclaration ParseSchema()
        {
            Expect(TokenKind.Schema, "Expected 'schema'.");
            var name = Expect(TokenKind.Identifier, "Expected schema name.");
            Expect(TokenKind.Equals, "Expected '=' in schema declaration.");
            SkipNewLines();
            Expect(TokenKind.LBrace, "Expected '{' to start schema body.");

            var fields = new List<ParameterDeclaration>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RBrace))
            {
                SkipNewLines();
                if (At(TokenKind.RBrace))
                {
                    break;
                }

                var fieldName = Expect(TokenKind.Identifier, "Expected schema field name.");
                Expect(TokenKind.Colon, "Expected ':' after schema field name.");
                var fieldType = ReadSchemaFieldType();
                fields.Add(new ParameterDeclaration(fieldName.Text, fieldType));

                if (At(TokenKind.Comma))
                {
                    Advance();
                }
                // Do NOT call ConsumeLineRemainder() here: it would consume subsequent
                // fields or the closing '}' in single-line schemas such as
                //   schema S = { id: string, status: OrderStatus }
                // The SkipNewLines() at the top of the loop handles the multi-line case.
            }

            Expect(TokenKind.RBrace, "Expected '}' to end schema body.");
            ConsumeLineRemainder();
            return new SchemaDeclaration(name.Text, fields, name.Location);
        }

        private string ReadSchemaFieldType()
        {
            var start = Current.Location.Offset;
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.Comma) && !At(TokenKind.NewLine) && !At(TokenKind.RBrace))
            {
                Advance();
            }

            var end = Current.Location.Offset;
            return StripInlineComment(_source[start..end]);
        }

        private ToolDeclaration ParseTool()
        {
            Expect(TokenKind.Tool, "Expected 'tool'.");
            var name = Expect(TokenKind.Identifier, "Expected tool name.");
            var parameters = ParseParameters();
            SkipNewLines();
            Expect(TokenKind.Arrow, "Expected '->' before tool return type.");
            var returnType = ReadUntilLineEnd();

            string? sandbox = null;
            string? timeout = null;
            var capabilities = new List<string>();

            while (!At(TokenKind.EndOfFile))
            {
                SkipNewLines();
                if (At(TokenKind.EndOfFile) || IsTopLevelDeclaration(Current.Kind))
                {
                    break;
                }

                var property = Expect(TokenKind.Identifier, "Expected tool property such as sandbox, capabilities, or timeout.");
                Expect(TokenKind.Equals, $"Expected '=' after tool property '{property.Text}'.");
                var value = ReadUntilLineEnd();

                switch (property.Text)
                {
                    case "sandbox":
                        sandbox = value.Trim();
                        if (!IsIdentifierText(sandbox))
                        {
                            Report("AI2033", $"Tool '{name.Text}' sandbox must be a bare sandbox identifier such as 'wasm'.", property.Location);
                        }
                        break;
                    case "capabilities":
                        capabilities.AddRange(ParseCapabilityListValue(value, property.Location, name.Text));
                        break;
                    case "timeout":
                        timeout = value.Trim();
                        break;
                    default:
                        Report("AI1010", $"Unsupported tool property '{property.Text}'. Supported properties are sandbox, capabilities, and timeout.", property.Location);
                        break;
                }
            }

            return new ToolDeclaration(name.Text, parameters, returnType.Trim(), sandbox, capabilities, timeout, name.Location);
        }

        private MacroDeclaration ParseMacro()
        {
            Expect(TokenKind.Macro, "Expected 'macro'.");
            var name = Expect(TokenKind.Identifier, "Expected macro name.");
            var parameters = ParseParameters();
            SkipNewLines();
            Expect(TokenKind.Arrow, "Expected '->' before macro return type.");
            var returnType = ReadUntilLineEnd();

            string? input = null;
            string? expands = null;
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.LBrace))
            {
                SkipNewLines();
                if (At(TokenKind.LBrace))
                {
                    break;
                }

                var property = Expect(TokenKind.Identifier, "Expected macro property such as input or expands.");
                Expect(TokenKind.Equals, $"Expected '=' after macro property '{property.Text}'.");
                var value = ReadUntilLineEnd().Trim();

                switch (property.Text)
                {
                    case "input":
                        input = value;
                        break;
                    case "expands":
                        expands = value;
                        break;
                    default:
                        Report("AI2045", $"Unsupported macro property '{property.Text}'. Supported properties are input and expands.", property.Location);
                        break;
                }
            }

            var body = ParseBalancedBlock("macro body");
            var compileTimeBody = new CompileTimeBody(body.Text, LocationFromOffset(body.Offset));
            ReportCompileTimeSideEffects("macro", name.Text, compileTimeBody);
            return new MacroDeclaration(name.Text, parameters, returnType.Trim(), input, expands, compileTimeBody, name.Location);
        }

        private GeneratorDeclaration ParseGenerator()
        {
            Expect(TokenKind.Generator, "Expected 'generator'.");
            var name = Expect(TokenKind.Identifier, "Expected generator name.");
            var parameters = ParseParameters();
            SkipNewLines();
            Expect(TokenKind.Arrow, "Expected '->' before generator return type.");
            var returnType = ReadUntilLineEnd();

            var incremental = false;
            var deterministic = false;
            var tracks = new List<string>();
            string? output = null;
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.LBrace))
            {
                SkipNewLines();
                if (At(TokenKind.LBrace))
                {
                    break;
                }

                var property = Expect(TokenKind.Identifier, "Expected generator property such as incremental, deterministic, tracks, or output.");
                Expect(TokenKind.Equals, $"Expected '=' after generator property '{property.Text}'.");
                var value = ReadUntilLineEnd().Trim();

                switch (property.Text)
                {
                    case "incremental":
                        incremental = ParseBooleanProperty(value, property.Location, $"Generator '{name.Text}' incremental");
                        break;
                    case "deterministic":
                        deterministic = ParseBooleanProperty(value, property.Location, $"Generator '{name.Text}' deterministic");
                        break;
                    case "tracks":
                        tracks.AddRange(ParseBracketedIdentifierListValue(value, property.Location, $"Generator '{name.Text}' tracks"));
                        break;
                    case "output":
                        output = NormalizeStringOrIdentifierValue(value);
                        break;
                    default:
                        Report("AI2046", $"Unsupported generator property '{property.Text}'. Supported properties are incremental, deterministic, tracks, and output.", property.Location);
                        break;
                }
            }

            var body = ParseBalancedBlock("generator body");
            var compileTimeBody = new CompileTimeBody(body.Text, LocationFromOffset(body.Offset));
            ReportCompileTimeSideEffects("generator", name.Text, compileTimeBody);
            return new GeneratorDeclaration(name.Text, parameters, returnType.Trim(), incremental, deterministic, tracks, output, compileTimeBody, name.Location);
        }

        private FunctionDeclaration ParseAsyncFunction()
        {
            Expect(TokenKind.Async, "Expected 'async'.");
            return ParseFunction(isAsync: true);
        }

        private ActorDeclaration ParseActor()
        {
            Expect(TokenKind.Actor, "Expected 'actor'.");
            var name = Expect(TokenKind.Identifier, "Expected actor name.");
            SkipNewLines();
            Expect(TokenKind.LBrace, "Expected '{' to start actor body.");

            var stateMembers = new List<ActorStateDeclaration>();
            var handlers = new List<FunctionDeclaration>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RBrace))
            {
                SkipNewLines();
                if (At(TokenKind.RBrace))
                {
                    break;
                }

                if (At(TokenKind.State))
                {
                    stateMembers.Add(ParseActorState());
                    continue;
                }

                if (At(TokenKind.Async))
                {
                    handlers.Add(ParseAsyncFunction());
                    continue;
                }

                if (At(TokenKind.Fn))
                {
                    var location = Current.Location;
                    handlers.Add(ParseFunction(isAsync: false));
                    Report("AI2038", $"Actor '{name.Text}' handler '{handlers[^1].Name}' must be declared async.", location);
                    continue;
                }

                Report("AI2035", $"Expected actor state member or async handler in actor '{name.Text}'.", Current.Location);
                ConsumeLineRemainder();
            }

            Expect(TokenKind.RBrace, "Expected '}' to end actor body.");
            ConsumeLineRemainder();

            if (stateMembers.Count == 0)
            {
                Report("AI2036", $"Actor '{name.Text}' must declare at least one state member.", name.Location);
            }

            if (handlers.Count == 0)
            {
                Report("AI2037", $"Actor '{name.Text}' must declare at least one async handler.", name.Location);
            }

            return new ActorDeclaration(name.Text, stateMembers, handlers, name.Location);
        }

        private ActorStateDeclaration ParseActorState()
        {
            Expect(TokenKind.State, "Expected 'state'.");
            var name = Expect(TokenKind.Identifier, "Expected actor state member name.");
            Expect(TokenKind.Colon, $"Expected ':' after actor state member '{name.Text}'.");
            var type = ReadSchemaFieldType();
            ConsumeLineRemainder();
            return new ActorStateDeclaration(name.Text, type, name.Location);
        }

        private FunctionDeclaration ParseFunction(bool isAsync)
        {
            Expect(TokenKind.Fn, "Expected 'fn'.");
            var name = Expect(TokenKind.Identifier, "Expected function name.");
            var parameters = ParseParameters();
            SkipNewLines();
            Expect(TokenKind.Arrow, "Expected '->' before function return type.");
            var returnType = ReadUntilLineEnd();

            var effects = new List<string>();
            var contracts = new List<ContractDeclaration>();

            while (!At(TokenKind.EndOfFile) && !At(TokenKind.LBrace))
            {
                SkipNewLines();
                if (At(TokenKind.Effect))
                {
                    effects.AddRange(ParseEffectList(requireParentheses: true, allowBuiltInEffects: true));
                    continue;
                }

                if (At(TokenKind.Effects))
                {
                    effects.AddRange(ParseBracketedEffectList());
                    continue;
                }

                if (At(TokenKind.Requires) || At(TokenKind.Ensures) || At(TokenKind.Invariant))
                {
                    contracts.Add(ParseContract());
                    continue;
                }

                if (!At(TokenKind.LBrace))
                {
                    Report("AI1005", "Expected function effect, contract, or body.", Current.Location);
                    ConsumeLineRemainder();
                }
            }

            if (contracts.Count == 0)
            {
                Report("AI1006", $"Function '{name.Text}' must declare at least one requires, ensures, or invariant contract.", name.Location);
            }

            var normalizedReturnType = returnType.Trim();
            var body = ParseBalancedBlock("function body");
            var parsedBody = ParseFunctionBody(body, normalizedReturnType);
            var modelCalls = ParseModelCalls(body);
            return new FunctionDeclaration(name.Text, parameters, normalizedReturnType, effects, contracts, parsedBody, modelCalls, name.Location, isAsync);
        }

        private IReadOnlyList<ParameterDeclaration> ParseParameters()
        {
            var parameters = new List<ParameterDeclaration>();
            Expect(TokenKind.LParen, "Expected '(' after function name.");

            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RParen))
            {
                var name = ExpectParameterName();
                Expect(TokenKind.Colon, "Expected ':' after parameter name.");
                var type = ReadParameterType();
                var (normalizedType, ownership) = ParseParameterTypeAnnotation(type);
                if (!string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(normalizedType))
                {
                    parameters.Add(new ParameterDeclaration(name.Text, normalizedType, ownership) { NameLocation = name.Location });
                }

                if (At(TokenKind.Comma))
                {
                    Advance();
                }
            }

            Expect(TokenKind.RParen, "Expected ')' after parameter list.");
            return parameters;
        }

        private Token ExpectParameterName()
        {
            if (At(TokenKind.Identifier) || At(TokenKind.Module))
            {
                return Advance();
            }

            Report("AI9999", "Expected parameter name.", Current.Location);
            return new Token(TokenKind.Identifier, string.Empty, Current.Location);
        }

        private string ReadParameterType()
        {
            var start = Current.Location.Offset;
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.Comma) && !At(TokenKind.RParen))
            {
                Advance();
            }

            var end = Current.Location.Offset;
            return StripInlineComment(_source[start..end]);
        }

        private ContractDeclaration ParseContract()
        {
            var token = Current;
            Advance();
            var expressionLocation = Current.Location;
            var expression = ReadUntilLineEnd();
            var kind = token.Kind switch
            {
                TokenKind.Requires => ContractKind.Requires,
                TokenKind.Ensures => ContractKind.Ensures,
                TokenKind.Invariant => ContractKind.Invariant,
                _ => throw new InvalidOperationException($"Token '{token.Kind}' is not a contract token.")
            };

            if (string.IsNullOrWhiteSpace(expression))
            {
                Report("AI2028", $"Contract '{token.Text}' must include a boolean expression for runtime checks and CI verification.", token.Location);
            }

            return new ContractDeclaration(kind, expression.Trim(), token.Location) { ExpressionLocation = expressionLocation };
        }

        private TestDeclaration ParseTest()
        {
            Expect(TokenKind.Test, "Expected 'test'.");
            var name = Expect(TokenKind.String, "Expected test name.");
            ParseBalancedBlock("test body");
            return new TestDeclaration(name.Text, name.Location);
        }

        private IReadOnlyList<ObservationDeclaration> ParseObserveBlock()
        {
            Expect(TokenKind.Observe, "Expected 'observe'.");
            Expect(TokenKind.LBrace, "Expected '{' after observe.");

            var observations = new List<ObservationDeclaration>();
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.RBrace))
            {
                SkipNewLines();
                if (At(TokenKind.RBrace))
                {
                    break;
                }

                var kind = Expect(TokenKind.Identifier, "Expected observation kind such as counter or histogram.");
                var name = Expect(TokenKind.String, "Expected observation metric name.");
                observations.Add(new ObservationDeclaration(kind.Text, name.Text, kind.Location));
                ConsumeLineRemainder();
            }

            Expect(TokenKind.RBrace, "Expected '}' after observe block.");
            ConsumeLineRemainder();
            return observations;
        }

        private SourceBlock ParseBalancedBlock(string description)
        {
            SkipNewLines();
            if (!At(TokenKind.LBrace))
            {
                Report("AI1007", $"Expected '{{' to start {description}.", Current.Location);
                return new SourceBlock(string.Empty, Current.Location.Offset);
            }

            var depth = 0;
            var bodyStart = Current.Location.Offset + Current.Text.Length;
            var bodyEnd = bodyStart;
            do
            {
                if (At(TokenKind.LBrace))
                {
                    depth++;
                }
                else if (At(TokenKind.RBrace))
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyEnd = Current.Location.Offset;
                    }
                }

                Advance();
            }
            while (depth > 0 && !At(TokenKind.EndOfFile));

            if (depth > 0)
            {
                Report("AI1008", $"Unterminated {description}.", Current.Location);
            }

            ConsumeLineRemainder();
            return new SourceBlock(_source[bodyStart..Math.Max(bodyStart, bodyEnd)], bodyStart);
        }

        private FunctionBody ParseFunctionBody(SourceBlock body, string returnType)
        {
            if (_profile == ScalarLanguage.StrictProfile)
                return new FunctionBody([]) { ScalarSource = new(body.Text, LocationFromOffset(body.Offset)) };
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return new FunctionBody([]);
            }

            var lines = ReadBodyLines(body);
            var index = 0;
            return new FunctionBody(ParseFunctionBodyStatements(lines, ref index, returnType, stopAtClosingBrace: false));
        }

        private IReadOnlyList<FunctionStatement> ParseFunctionBodyStatements(IReadOnlyList<BodyLine> lines, ref int index, string returnType, bool stopAtClosingBrace)
        {
            var statements = new List<FunctionStatement>();
            var foundClosingBrace = false;
            while (index < lines.Count)
            {
                var line = lines[index];
                var trimmed = StripInlineComment(line.Text).Trim();
                if (trimmed.Length == 0)
                {
                    index++;
                    continue;
                }

                if (trimmed == "}")
                {
                    if (stopAtClosingBrace)
                    {
                        index++;
                        foundClosingBrace = true;
                        break;
                    }

                    Report("AI1021", "Unexpected '}' in function body.", LocationFromOffset(line.Offset));
                    index++;
                    continue;
                }

                if (trimmed.StartsWith("taskgroup", StringComparison.Ordinal)
                    && (trimmed.Length == "taskgroup".Length || char.IsWhiteSpace(trimmed["taskgroup".Length])))
                {
                    var taskGroup = ParseTaskGroupStatement(lines, ref index, returnType);
                    if (taskGroup is not null)
                    {
                        statements.Add(taskGroup);
                    }

                    continue;
                }

                if (trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.EndsWith("{"))
                {
                    var usingStatement = ParseUsingStatement(lines, ref index, returnType);
                    if (usingStatement is not null)
                    {
                        statements.Add(usingStatement);
                    }

                    continue;
                }

                if (trimmed.StartsWith("match ", StringComparison.Ordinal) && trimmed.EndsWith("{"))
                {
                    var matchStmt = ParseMatchStatement(lines, ref index);
                    if (matchStmt is not null)
                    {
                        statements.Add(matchStmt);
                    }

                    continue;
                }

                var statementLines = new List<BodyLine> { line };
                index++;
                while (index < lines.Count)
                {
                    var nextTrimmed = StripInlineComment(lines[index].Text).Trim();
                    if (nextTrimmed.Length == 0)
                    {
                        index++;
                        if (statementLines.Count > 0)
                        {
                            break;
                        }

                        continue;
                    }

                    if (nextTrimmed == "}")
                    {
                        break;
                    }

                    if (IsFunctionBodyStatementStart(nextTrimmed))
                    {
                        break;
                    }

                    statementLines.Add(lines[index]);
                    index++;
                }

                var statement = ParseFunctionStatement(statementLines, returnType);
                if (statement is not null)
                {
                    statements.Add(statement);
                }
            }

            if (stopAtClosingBrace && !foundClosingBrace)
            {
                Report("AI1021", "Unterminated taskgroup statement. Expected '}'.", lines.Count == 0 ? new SourceLocation(1, 1, 0) : LocationFromOffset(lines[^1].Offset + lines[^1].Text.Length));
            }

            return statements;
        }

        private FunctionStatement? ParseFunctionStatement(IReadOnlyList<BodyLine> lines, string returnType)
        {
            var first = lines[0];
            var firstText = StripInlineComment(first.Text).TrimStart();
            var location = LocationFromOffset(first.Offset + (first.Text.Length - first.Text.TrimStart().Length));

            if (firstText.StartsWith("let ", StringComparison.Ordinal))
            {
                return ParseLetStatement(lines, location);
            }

            if (firstText.StartsWith("return", StringComparison.Ordinal)
                && (firstText.Length == "return".Length || char.IsWhiteSpace(firstText["return".Length])))
            {
                var firstExpression = firstText["return".Length..].Trim();
                var expression = BuildExpressionText(lines, firstExpression);
                return new ReturnStatement(new ExpressionNode(expression, returnType, location), location);
            }

            if (firstText.StartsWith("spawn ", StringComparison.Ordinal))
            {
                return ParseSpawnStatement(lines, location);
            }

            if (firstText.StartsWith("all ", StringComparison.Ordinal))
            {
                return ParseAllStatement(lines, location);
            }

            var expressionText = BuildExpressionText(lines, firstText.Trim());
            return new ExpressionStatement(new ExpressionNode(expressionText, InferExpressionStaticType(expressionText), location), location);
        }

        private TaskGroupStatement? ParseTaskGroupStatement(IReadOnlyList<BodyLine> lines, ref int index, string returnType)
        {
            var line = lines[index];
            var trimmed = StripInlineComment(line.Text).Trim();
            var location = LocationFromOffset(line.Offset + (line.Text.Length - line.Text.TrimStart().Length));
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^taskgroup(?:\s+(?<name>[A-Za-z_][A-Za-z0-9_#]*))?\s*\{\s*$");

            index++;
            if (!match.Success)
            {
                Report("AI1021", "Expected taskgroup syntax 'taskgroup [name] {'.", location);
                return null;
            }

            var childStatements = ParseFunctionBodyStatements(lines, ref index, returnType, stopAtClosingBrace: true);
            var name = match.Groups["name"].Success ? match.Groups["name"].Value : null;
            return new TaskGroupStatement(name, childStatements, location);
        }

        private UsingStatement? ParseUsingStatement(IReadOnlyList<BodyLine> lines, ref int index, string returnType)
        {
            var line = lines[index];
            var trimmed = StripInlineComment(line.Text).Trim();
            var location = LocationFromOffset(line.Offset + (line.Text.Length - line.Text.TrimStart().Length));
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^using\s+(?<name>[A-Za-z_][A-Za-z0-9_#]*)\s*\{\s*$");

            index++;
            if (!match.Success)
            {
                Report("AI1027", "Expected using syntax 'using resourceName {'.", location);
                return null;
            }

            var childStatements = ParseFunctionBodyStatements(lines, ref index, returnType, stopAtClosingBrace: true);
            return new UsingStatement(match.Groups["name"].Value, childStatements, location);
        }

        private MatchStatement? ParseMatchStatement(IReadOnlyList<BodyLine> lines, ref int index)
        {
            var line = lines[index];
            var trimmed = StripInlineComment(line.Text).Trim();
            var location = LocationFromOffset(line.Offset + (line.Text.Length - line.Text.TrimStart().Length));
            var headerMatch = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^match\s+(?<scrutinee>[^{]+?)\s*\{\s*$");

            index++;
            if (!headerMatch.Success)
            {
                Report("AI1025", "Expected match syntax 'match <scrutinee> {'.", location);
                return null;
            }

            var scrutineeText = headerMatch.Groups["scrutinee"].Value.Trim();
            var arms = new List<MatchArm>();

            while (index < lines.Count)
            {
                var armLine = lines[index];
                var armTrimmed = StripInlineComment(armLine.Text).Trim();
                if (armTrimmed == "}")
                {
                    index++;
                    break;
                }

                var armLocation = LocationFromOffset(armLine.Offset + (armLine.Text.Length - armLine.Text.TrimStart().Length));
                var armMatch = System.Text.RegularExpressions.Regex.Match(
                    armTrimmed,
                    @"^(?<pattern>[A-Za-z_][A-Za-z0-9_#]*|_)\s*=>\s*(?<body>.+)$");

                if (!armMatch.Success)
                {
                    Report("AI1026", $"Expected match arm syntax 'Pattern => expression', got: {armTrimmed}", armLocation);
                    index++;
                    continue;
                }

                var patternText = armMatch.Groups["pattern"].Value;
                var bodyText = NormalizeExpressionText(armMatch.Groups["body"].Value.Trim());
                arms.Add(new MatchArm(patternText, bodyText, armLocation));
                index++;
            }

            return new MatchStatement(scrutineeText, arms, location);
        }

        private FunctionStatement? ParseSpawnStatement(IReadOnlyList<BodyLine> lines, SourceLocation location)
        {
            var statementText = string.Join('\n', lines.Select(line => StripInlineComment(line.Text).Trim()));
            var match = System.Text.RegularExpressions.Regex.Match(
                statementText,
                @"^spawn\s+(?<name>[A-Za-z_][A-Za-z0-9_#]*)\s*=\s*(?<expr>.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (!match.Success)
            {
                Report("AI1022", "Expected spawn syntax 'spawn name = expression'.", location);
                return null;
            }

            var expressionText = NormalizeExpressionText(match.Groups["expr"].Value);
            if (string.IsNullOrWhiteSpace(expressionText))
            {
                Report("AI1022", "Spawn statement must include an expression.", location);
            }

            return new SpawnStatement(match.Groups["name"].Value, new ExpressionNode(expressionText, InferExpressionStaticType(expressionText), location), location);
        }

        private FunctionStatement? ParseAllStatement(IReadOnlyList<BodyLine> lines, SourceLocation location)
        {
            var firstExpression = StripInlineComment(lines[0].Text).TrimStart()["all".Length..].Trim();
            var text = BuildExpressionText(lines, firstExpression);
            var targets = text
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();

            if (targets.Length == 0)
            {
                Report("AI1023", "All statement must name at least one spawned task.", location);
            }

            foreach (var target in targets)
            {
                if (!IsIdentifierText(target))
                {
                    Report("AI1023", $"All statement target '{target}' must be an identifier.", location);
                }
            }

            return new AllStatement(targets, location);
        }

        private LetStatement? ParseLetStatement(IReadOnlyList<BodyLine> lines, SourceLocation location)
        {
            var statementText = string.Join('\n', lines.Select(line => StripInlineComment(line.Text).Trim()));
            var match = System.Text.RegularExpressions.Regex.Match(
                statementText,
                @"^let\s+(?<name>[A-Za-z_][A-Za-z0-9_#]*)(?:\s*:\s*(?<type>[^=\n]+?))?\s*=\s*(?<expr>.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (!match.Success)
            {
                Report("AI1020", "Expected let binding syntax 'let name[: Type] = expression'.", location);
                return null;
            }

            var name = match.Groups["name"].Value;
            var typeAnnotation = match.Groups["type"].Success ? match.Groups["type"].Value.Trim() : null;
            var expressionText = NormalizeExpressionText(match.Groups["expr"].Value);
            var staticType = string.IsNullOrWhiteSpace(typeAnnotation)
                ? InferExpressionStaticType(expressionText)
                : typeAnnotation;

            return new LetStatement(name, typeAnnotation, new ExpressionNode(expressionText, staticType, location), location);
        }

        private static string BuildExpressionText(IReadOnlyList<BodyLine> lines, string firstExpression)
        {
            var expressionLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(firstExpression))
            {
                expressionLines.Add(firstExpression.Trim());
            }

            foreach (var line in lines.Skip(1))
            {
                var trimmed = StripInlineComment(line.Text).Trim();
                if (trimmed.Length > 0)
                {
                    expressionLines.Add(trimmed);
                }
            }

            return NormalizeExpressionText(string.Join('\n', expressionLines));
        }

        private static string NormalizeExpressionText(string expression) =>
            string.Join(
                '\n',
                expression
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.TrimEntries)
                    .Where(line => line.Length > 0));

        private static string? InferExpressionStaticType(string expression) => ReadOutputSchema(expression);

        private static bool IsFunctionBodyStatementStart(string trimmedLine) =>
            trimmedLine.StartsWith("let ", StringComparison.Ordinal)
            || trimmedLine == "return"
            || trimmedLine.StartsWith("return ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("taskgroup ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("using ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("spawn ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("all ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("match ", StringComparison.Ordinal);

        private static IReadOnlyList<BodyLine> ReadBodyLines(SourceBlock body)
        {
            var lines = new List<BodyLine>();
            var lineStart = 0;
            for (var index = 0; index <= body.Text.Length; index++)
            {
                if (index < body.Text.Length && body.Text[index] != '\n')
                {
                    continue;
                }

                var length = index - lineStart;
                var text = body.Text.Substring(lineStart, length).TrimEnd('\r');
                lines.Add(new BodyLine(text, body.Offset + lineStart));
                lineStart = index + 1;
            }

            return lines;
        }

        private IReadOnlyList<ModelCallDeclaration> ParseModelCalls(SourceBlock body)
        {
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return [];
            }

            var calls = new List<ModelCallDeclaration>();
            var modelPattern = new System.Text.RegularExpressions.Regex(
                @"\bmodel\s+(?<provider>[A-Za-z_][A-Za-z0-9_#]*)\s+""(?<model>(?:\\.|[^""])*)""(?<chain>.*?)(?:\.run\s*\(\s*\))",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var validMatchSpans = new List<(int Start, int End)>();
            foreach (System.Text.RegularExpressions.Match match in modelPattern.Matches(body.Text))
            {
                validMatchSpans.Add((match.Index, match.Index + match.Length));
                var provider = match.Groups["provider"].Value;
                var model = UnescapeStringLiteral(match.Groups["model"].Value);
                var chain = match.Groups["chain"].Value;
                var chainOffset = body.Offset + match.Groups["chain"].Index;
                var location = LocationFromOffset(body.Offset + match.Index);
                var outputSchemaReference = ReadOutputSchemaReference(chain, chainOffset);
                var outputSchema = outputSchemaReference?.Name;
                var task = ReadStringChainArgument(chain, "task");
                var input = ReadExpressionChainArgument(chain, "input");
                var replay = ReadReplay(chain);
                var capturesProvenance = chain.Contains(".capture_provenance()", StringComparison.Ordinal);
                if (outputSchema is null)
                {
                    Report("AI2031", "Model calls must declare a schema-constrained output with .output<Schema>() before .run().", location);
                }

                calls.Add(new ModelCallDeclaration(
                    provider,
                    model,
                    task,
                    input,
                    outputSchema,
                    replay,
                    capturesProvenance,
                    location,
                    outputSchemaReference,
                    capturesProvenance
                        ? new ProvenanceMetadataDeclaration(null, provider, model, replay, [], [])
                        : null));
            }

            ReportInvalidModelCallSyntax(body, validMatchSpans);

            return calls;
        }

        private void ReportInvalidModelCallSyntax(SourceBlock body, IReadOnlyList<(int Start, int End)> validMatchSpans)
        {
            var modelKeywordPattern = new System.Text.RegularExpressions.Regex(@"(?m)(?:^|(?<=[=\n]))[ \t]*(?<keyword>model)\b");
            foreach (System.Text.RegularExpressions.Match match in modelKeywordPattern.Matches(body.Text))
            {
                var keywordIndex = match.Groups["keyword"].Index;
                if (validMatchSpans.Any(span => keywordIndex >= span.Start && keywordIndex < span.End))
                {
                    continue;
                }

                var candidate = ReadModelCallCandidate(body.Text, keywordIndex);
                var location = LocationFromOffset(body.Offset + keywordIndex);
                var headerMatch = System.Text.RegularExpressions.Regex.Match(
                    candidate,
                    @"\Amodel\s+(?<provider>[A-Za-z_][A-Za-z0-9_#]*)(?<tail>.*)\z",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (!headerMatch.Success)
                {
                    Report("AI2030", "Model calls must specify a provider and a quoted model id after the model keyword.", location);
                    continue;
                }

                var tail = headerMatch.Groups["tail"].Value.TrimStart();
                if (!tail.StartsWith("\"", StringComparison.Ordinal))
                {
                    Report("AI2030", "Model calls must specify a quoted model id after the provider.", location);
                    continue;
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(candidate, @"\.run\s*\(\s*\)"))
                {
                    Report("AI2032", "Model call chains must end with a .run() invocation.", location);
                }
            }
        }

        private static string ReadModelCallCandidate(string bodyText, int modelKeywordIndex)
        {
            var remaining = bodyText[modelKeywordIndex..];
            var nextStatement = System.Text.RegularExpressions.Regex.Match(
                remaining,
                @"\n\s*(?:let|return)\b");
            return nextStatement.Success
                ? remaining[..nextStatement.Index].Trim()
                : remaining.Trim();
        }

        private static string? ReadStringChainArgument(string chain, string method)
        {
            var pattern = new System.Text.RegularExpressions.Regex(@"\." + method + @"\s*\(\s*""(?<value>(?:\\.|[^""])*)""\s*\)");
            var match = pattern.Match(chain);
            return match.Success ? UnescapeStringLiteral(match.Groups["value"].Value) : null;
        }

        private static string? ReadExpressionChainArgument(string chain, string method)
        {
            var pattern = new System.Text.RegularExpressions.Regex(@"\." + method + @"\s*\(\s*(?<value>[^)]*?)\s*\)");
            var match = pattern.Match(chain);
            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }

        private static string? ReadOutputSchema(string chain)
        {
            var match = System.Text.RegularExpressions.Regex.Match(chain, @"\.output\s*<\s*(?<schema>[A-Za-z_][A-Za-z0-9_#]*(?:\s*<[^>]+>)?)\s*>\s*\(\s*\)");
            return match.Success ? match.Groups["schema"].Value.Trim() : null;
        }

        private SchemaReferenceDeclaration? ReadOutputSchemaReference(string chain, int chainOffset)
        {
            var match = System.Text.RegularExpressions.Regex.Match(chain, @"\.output\s*<\s*(?<schema>[A-Za-z_][A-Za-z0-9_#]*(?:\s*<[^>]+>)?)\s*>\s*\(\s*\)");
            if (!match.Success)
            {
                return null;
            }

            return new SchemaReferenceDeclaration(match.Groups["schema"].Value.Trim(), LocationFromOffset(chainOffset + match.Index));
        }

        private static ModelReplayDeclaration? ReadReplay(string chain)
        {
            var arguments = ReadExpressionChainArgument(chain, "replay");
            if (arguments is null)
            {
                return null;
            }

            return new ModelReplayDeclaration(
                arguments,
                ReadIntegerReplayArgument(arguments, "seed"),
                ReadDecimalReplayArgument(arguments, "temperature"));
        }

        private static int? ReadIntegerReplayArgument(string arguments, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                arguments,
                @"(?:^|,)\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*:\s*(?<value>-?\d+)(?:\s*,|\s*$)");
            return match.Success
                && int.TryParse(match.Groups["value"].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null;
        }

        private static decimal? ReadDecimalReplayArgument(string arguments, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                arguments,
                @"(?:^|,)\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*:\s*(?<value>-?(?:\d+(?:\.\d+)?|\.\d+))(?:\s*,|\s*$)");
            return match.Success
                && decimal.TryParse(match.Groups["value"].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null;
        }

        private static string UnescapeStringLiteral(string value) =>
            value.Replace("\\\"", "\"", StringComparison.Ordinal);

        private void ValidateDocument(AISharpDocument document)
        {
            if (string.IsNullOrWhiteSpace(document.Intent))
            {
                Report("AI2001", "Document must declare an intent string.", new SourceLocation(1, 1, 0));
            }

            var duplicateUses = document.Uses
                .GroupBy(u => u.Path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateUses)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2019", $"Use path '{group.Key}' is declared more than once.", location);
            }

            var duplicateTypes = document.Types
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateTypes)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2002", $"Type '{group.Key}' is declared more than once.", location);
            }

            var duplicatePolicies = document.Policies
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicatePolicies)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2011", $"Policy '{group.Key}' is declared more than once.", location);
            }

            foreach (var policy in document.Policies)
            {
                ValidatePolicyFields(policy);
            }

            ValidatePolicySecurityPosture(document);

            var duplicateCapabilities = document.Capabilities
                .GroupBy(c => c.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateCapabilities)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2003", $"Capability '{group.Key}' is declared more than once.", location);
            }

            var duplicateResources = document.Resources
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateResources)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2009", $"Resource '{group.Key}' is declared more than once.", location);
            }

            var duplicateFunctions = document.Functions
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateFunctions)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2006", $"Function '{group.Key}' is declared more than once.", location);
            }

            var duplicateActors = document.Actors
                .GroupBy(a => a.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateActors)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2039", $"Actor '{group.Key}' is declared more than once.", location);
            }

            foreach (var actor in document.Actors)
            {
                var duplicateStateMembers = actor.StateMembers
                    .GroupBy(s => s.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1);

                foreach (var group in duplicateStateMembers)
                {
                    Report("AI2040", $"Actor '{actor.Name}' declares state member '{group.Key}' more than once.", group.Skip(1).First().Location);
                }

                var duplicateHandlers = actor.Handlers
                    .GroupBy(h => h.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1);

                foreach (var group in duplicateHandlers)
                {
                    Report("AI2041", $"Actor '{actor.Name}' declares handler '{group.Key}' more than once.", group.Skip(1).First().Location);
                }
            }

            foreach (var type in document.Types)
            {
                var duplicateCases = type.Cases
                    .GroupBy(c => c, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1);

                foreach (var group in duplicateCases)
                {
                    Report("AI2007", $"Union type '{type.Name}' declares case '{group.Key}' more than once.", type.Location);
                }
            }

            var duplicateSchemas = document.Schemas
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateSchemas)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2012", $"Schema '{group.Key}' is declared more than once.", location);
            }

            var declaredSchemas = document.Schemas
                .Select(s => s.Name)
                .ToHashSet(StringComparer.Ordinal);

            var duplicateTools = document.Tools
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateTools)
            {
                var location = group.Skip(1).First().Location;
                Report("AI2016", $"Tool '{group.Key}' is declared more than once.", location);
            }

            foreach (var tool in document.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Sandbox))
                {
                    Report("AI2013", $"Tool '{tool.Name}' must declare a sandbox. Use 'sandbox = wasm' for untrusted tool execution.", tool.Location);
                }
                else if (!string.Equals(tool.Sandbox, "wasm", StringComparison.Ordinal))
                {
                    Report("AI2015", $"Tool '{tool.Name}' declares unsupported sandbox '{tool.Sandbox}'. AI# tools must use the wasm sandbox.", tool.Location);
                }

                if (string.IsNullOrWhiteSpace(tool.Timeout))
                {
                    Report("AI2014", $"Tool '{tool.Name}' must declare a timeout such as 'timeout = 30s'.", tool.Location);
                }
            }

            var declaredCapabilities = document.Capabilities
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);
            var declaredResources = document.Resources
                .Select(r => r.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var function in GetAllFunctions(document))
            {
                foreach (var modelCall in function.ModelCalls)
                {
                    if (!string.IsNullOrWhiteSpace(modelCall.OutputSchema)
                        && !declaredSchemas.Contains(modelCall.OutputSchema))
                    {
                        Report("AI2026", $"Function '{function.Name}' model call references undeclared output schema '{modelCall.OutputSchema}'. Declare a schema before using it in .output<...>().", modelCall.Location);
                    }
                }

                foreach (var parameter in function.Parameters)
                {
                    var resourceTypeName = GetDeclaredTypeName(parameter.Type);
                    var isDeclaredResource = declaredResources.Contains(resourceTypeName);
                    if (parameter.Ownership != ParameterOwnership.Unspecified
                        && !isDeclaredResource
                        && !LooksLikeResourceType(resourceTypeName))
                    {
                        Report("AI2029", $"Function '{function.Name}' parameter '{parameter.Name}' uses ownership qualifier '{FormatOwnership(parameter.Ownership)}' on non-resource type '{parameter.Type}'. Declare '{resourceTypeName}' as a resource or remove the ownership qualifier.", FindParameterTypeLocation(function, parameter));
                    }

                    if (LooksLikeCapabilityType(parameter.Type) && !declaredCapabilities.Contains(parameter.Type))
                    {
                        Report("AI2008", $"Function '{function.Name}' parameter '{parameter.Name}' uses undeclared capability '{parameter.Type}'. Declare it at document scope before using it as a parameter.", FindParameterTypeLocation(function, parameter));
                    }

                    if (LooksLikeResourceType(resourceTypeName) && !declaredResources.Contains(resourceTypeName))
                    {
                        Report("AI2010", $"Function '{function.Name}' parameter '{parameter.Name}' uses undeclared resource '{parameter.Type}'. Declare it at document scope before using it as a parameter.", FindParameterTypeLocation(function, parameter));
                    }
                }
            }

            var declaredEffects = document.Effects.ToHashSet(StringComparer.Ordinal);
            declaredEffects.UnionWith(BuiltInEffectNames);
            foreach (var function in GetAllFunctions(document))
            {
                foreach (var effect in function.Effects)
                {
                    if (!declaredEffects.Contains(effect))
                    {
                        Report("AI2004", $"Function '{function.Name}' uses undeclared effect '{effect}'. Declare it at document scope before using it in a function.", function.Location);
                    }
                }
            }

            foreach (var observation in document.Observability)
            {
                if (!IsSupportedObservationKind(observation.Kind))
                {
                    Report("AI2005", $"Unsupported observability declaration '{observation.Kind}'. Supported kinds are counter and histogram.", observation.Location);
                }
            }

            foreach (var tool in document.Tools)
            {
                foreach (var capRef in tool.Capabilities)
                {
                    if (!declaredCapabilities.Contains(capRef))
                    {
                        Report("AI2017", $"Tool '{tool.Name}' references undeclared capability '{capRef}'. Declare it at document scope before referencing it in a tool.", tool.Location);
                    }
                }
            }
        }

        private static IEnumerable<FunctionDeclaration> GetAllFunctions(AISharpDocument document) =>
            document.Functions.Concat(document.Actors.SelectMany(actor => actor.Handlers));

        private void ValidatePolicyFields(PolicyDeclaration policy)
        {
            var duplicateFields = policy.Fields
                .GroupBy(f => f.Key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicateFields)
            {
                Report("AI2020", $"Policy '{policy.Name}' declares field '{group.Key}' more than once.", policy.Location);
            }

            foreach (var field in policy.Fields)
            {
                if (!IsSupportedPolicyField(field.Key))
                {
                    Report("AI2021", $"Policy '{policy.Name}' declares unsupported field '{field.Key}'. Supported fields are classification, inference, training, telemetry, external_text, and capabilities.", policy.Location);
                    continue;
                }

                if (string.Equals(field.Key, "inference", StringComparison.Ordinal)
                    && !IsSupportedPolicyInferenceValue(field.Value))
                {
                    Report("AI2022", $"Policy '{policy.Name}' declares unsupported inference value '{field.Value}'. Use local_only by default or remote_opt_in for explicit remote inference.", policy.Location);
                }

                if (string.Equals(field.Key, "classification", StringComparison.Ordinal)
                    && !IsSupportedPolicyClassificationValue(field.Value))
                {
                    Report("AI2023", $"Policy '{policy.Name}' declares unsupported classification value '{field.Value}'. Supported classification values are public, internal, and secret.", policy.Location);
                }

                if (string.Equals(field.Key, "training", StringComparison.Ordinal)
                    && !IsSupportedPolicyTrainingValue(field.Value))
                {
                    Report("AI2024", $"Policy '{policy.Name}' declares unsupported training value '{field.Value}'. Use forbidden by default or opt_in for explicit training use.", policy.Location);
                }

                if (string.Equals(field.Key, "telemetry", StringComparison.Ordinal)
                    && !IsSupportedPolicyTelemetryValue(field.Value))
                {
                    Report("AI2025", $"Policy '{policy.Name}' declares unsupported telemetry value '{field.Value}'. Use forbidden or opt_in_aggregate for telemetry collection.", policy.Location);
                }

                if (string.Equals(field.Key, "external_text", StringComparison.Ordinal)
                    && !IsSupportedPolicyExternalTextValue(field.Value))
                {
                    Report("AI2052", $"Policy '{policy.Name}' declares unsupported external_text value '{field.Value}'. External text must be modeled as tainted.", policy.Location);
                }

                if (string.Equals(field.Key, "capabilities", StringComparison.Ordinal)
                    && !IsSupportedPolicyCapabilitiesValue(field.Value))
                {
                    Report("AI2053", $"Policy '{policy.Name}' declares unsupported capabilities value '{field.Value}'. Model prompts and tool capabilities must remain separated.", policy.Location);
                }
            }
        }

        private void ValidatePolicySecurityPosture(AISharpDocument document)
        {
            var hasRemoteOptIn = document.Policies.Any(policy =>
                string.Equals(policy.Inference, "remote_opt_in", StringComparison.Ordinal));

            foreach (var policy in document.Policies.Where(policy =>
                string.Equals(policy.Inference, "remote_opt_in", StringComparison.Ordinal)))
            {
                if (policy.ExternalText is null)
                {
                    Report("AI2050", $"Policy '{policy.Name}' opts into remote inference and must declare external_text = tainted so untrusted prompt material is tracked explicitly.", policy.Location);
                }

                if (policy.Capabilities is null)
                {
                    Report("AI2051", $"Policy '{policy.Name}' opts into remote inference and must declare capabilities = separated so tool privileges are never granted through prompt text.", policy.Location);
                }
            }

            foreach (var function in GetAllFunctions(document))
            {
                foreach (var modelCall in function.ModelCalls.Where(call => !IsLocalModelProvider(call.Provider)))
                {
                    if (!hasRemoteOptIn)
                    {
                        Report("AI2049", $"Function '{function.Name}' uses remote model provider '{modelCall.Provider}' but no policy declares inference = remote_opt_in. AI# defaults to local-only inference for protected source, logs, secrets, and internal artifacts.", modelCall.Location);
                    }
                }
            }
        }

        private static bool IsSupportedPolicyField(string field) =>
            string.Equals(field, "classification", StringComparison.Ordinal)
            || string.Equals(field, "inference", StringComparison.Ordinal)
            || string.Equals(field, "training", StringComparison.Ordinal)
            || string.Equals(field, "telemetry", StringComparison.Ordinal)
            || string.Equals(field, "external_text", StringComparison.Ordinal)
            || string.Equals(field, "capabilities", StringComparison.Ordinal);

        private static bool IsSupportedPolicyInferenceValue(string value) =>
            string.Equals(value.Trim(), "local_only", StringComparison.Ordinal)
            || string.Equals(value.Trim(), "remote_opt_in", StringComparison.Ordinal);

        private static bool IsSupportedPolicyClassificationValue(string value) =>
            string.Equals(value.Trim(), "public", StringComparison.Ordinal)
            || string.Equals(value.Trim(), "internal", StringComparison.Ordinal)
            || string.Equals(value.Trim(), "secret", StringComparison.Ordinal);

        private static bool IsSupportedPolicyTrainingValue(string value) =>
            string.Equals(value.Trim(), "forbidden", StringComparison.Ordinal)
            || string.Equals(value.Trim(), "opt_in", StringComparison.Ordinal);

        private static bool IsSupportedPolicyTelemetryValue(string value) =>
            string.Equals(value.Trim(), "forbidden", StringComparison.Ordinal)
            || string.Equals(value.Trim(), "opt_in_aggregate", StringComparison.Ordinal);

        private static bool IsSupportedPolicyExternalTextValue(string value) =>
            string.Equals(value.Trim(), "tainted", StringComparison.Ordinal);

        private static bool IsSupportedPolicyCapabilitiesValue(string value) =>
            string.Equals(value.Trim(), "separated", StringComparison.Ordinal);

        private static bool IsLocalModelProvider(string provider) =>
            string.Equals(provider, "local", StringComparison.Ordinal);

        private static bool IsSupportedObservationKind(string kind) =>
            string.Equals(kind, "counter", StringComparison.Ordinal)
            || string.Equals(kind, "histogram", StringComparison.Ordinal);

        private static readonly string[] BuiltInEffectNames =
        [
            "io",
            "time",
            "random",
            "model",
            "network",
            "unsafe_ffi"
        ];

        private static bool IsBuiltInEffect(string effect) =>
            BuiltInEffectNames.Contains(effect, StringComparer.Ordinal);

        private static bool IsTopLevelDeclaration(TokenKind kind) =>
            kind is TokenKind.Module
                or TokenKind.Use
                or TokenKind.Intent
                or TokenKind.Policy
                or TokenKind.Capability
                or TokenKind.Resource
                or TokenKind.Effect
                or TokenKind.Type
                or TokenKind.Schema
                or TokenKind.Tool
                or TokenKind.Macro
                or TokenKind.Generator
                or TokenKind.Fn
                or TokenKind.Async
                or TokenKind.Actor
                or TokenKind.Test
                or TokenKind.Observe;

        private bool ParseBooleanProperty(string value, SourceLocation location, string propertyName)
        {
            var trimmed = value.Trim();
            if (string.Equals(trimmed, "true", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(trimmed, "false", StringComparison.Ordinal))
            {
                return false;
            }

            Report("AI2047", $"{propertyName} must be 'true' or 'false'.", location);
            return false;
        }

        private IReadOnlyList<string> ParseBracketedIdentifierListValue(string value, SourceLocation location, string propertyName)
        {
            var trimmed = value.Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                Report("AI2048", $"{propertyName} must use bracketed identifier list syntax such as '[schemas, tools]'.", location);
                return [];
            }

            trimmed = trimmed[1..^1].Trim();
            if (trimmed.Length == 0)
            {
                return [];
            }

            var identifiers = new List<string>();
            foreach (var identifier in trimmed.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!IsIdentifierText(identifier))
                {
                    Report("AI2048", $"{propertyName} must contain only identifiers.", location);
                    return identifiers;
                }

                identifiers.Add(identifier);
            }

            return identifiers;
        }

        private static string NormalizeStringOrIdentifierValue(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
                ? UnescapeStringLiteral(trimmed[1..^1])
                : trimmed;
        }

        private void ReportCompileTimeSideEffects(string declarationKind, string declarationName, CompileTimeBody body)
        {
            if (!ContainsCompileTimeSideEffect(body.Text))
            {
                return;
            }

            Report(
                "AI2044",
                $"Compile-time {declarationKind} '{declarationName}' must be deterministic and cannot perform time, random, model, network, file, environment, or unsafe FFI side effects.",
                body.Location);
        }

        private static bool ContainsCompileTimeSideEffect(string bodyText)
        {
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                return false;
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                bodyText,
                @"(?<![A-Za-z0-9_#])(?:time|random|model|network|file|env|unsafe_ffi)(?:\s+|[.(_])",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private IReadOnlyList<string> ParseCapabilityListValue(string value, SourceLocation location, string toolName)
        {
            var trimmed = value.Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                Report("AI2034", $"Tool '{toolName}' capabilities must use bracketed list syntax such as 'capabilities = [tmpfs]'.", location);
                return [];
            }

            trimmed = trimmed[1..^1].Trim();
            if (trimmed.Length == 0)
            {
                return [];
            }

            var capabilities = new List<string>();
            foreach (var capability in trimmed.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!IsIdentifierText(capability))
                {
                    Report("AI2034", $"Tool '{toolName}' capabilities must contain only capability identifiers.", location);
                    return capabilities;
                }

                capabilities.Add(capability);
            }

            return capabilities;
        }

        private static bool IsIdentifierText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!char.IsLetter(value[0]) && value[0] != '_')
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                if (!char.IsLetterOrDigit(value[i]) && value[i] is not '_' and not '#')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool LooksLikeCapabilityType(string type) =>
            GetDeclaredTypeName(type).EndsWith("Api", StringComparison.Ordinal)
            || GetDeclaredTypeName(type).EndsWith("Client", StringComparison.Ordinal)
            || GetDeclaredTypeName(type).EndsWith("Service", StringComparison.Ordinal);

        private static bool LooksLikeResourceType(string type) =>
            GetDeclaredTypeName(type).EndsWith("Session", StringComparison.Ordinal)
            || GetDeclaredTypeName(type).EndsWith("Handle", StringComparison.Ordinal)
            || GetDeclaredTypeName(type).EndsWith("Buffer", StringComparison.Ordinal)
            || GetDeclaredTypeName(type).EndsWith("Secret", StringComparison.Ordinal)
            || string.Equals(GetDeclaredTypeName(type), "File", StringComparison.Ordinal)
            || string.Equals(GetDeclaredTypeName(type), "Socket", StringComparison.Ordinal);

        private static string FormatOwnership(ParameterOwnership ownership) =>
            ownership switch
            {
                ParameterOwnership.Owned => "owned",
                ParameterOwnership.Borrowed => "borrowed",
                _ => "unspecified"
            };

        private static (string Type, ParameterOwnership Ownership) ParseParameterTypeAnnotation(string rawType)
        {
            var trimmed = rawType.Trim();
            if (TryConsumeOwnershipQualifier(trimmed, "owned", out var ownedType))
            {
                return (ownedType.Trim(), ParameterOwnership.Owned);
            }

            if (TryConsumeOwnershipQualifier(trimmed, "borrowed", out var borrowedType))
            {
                return (borrowedType.Trim(), ParameterOwnership.Borrowed);
            }

            return (trimmed, ParameterOwnership.Unspecified);
        }

        private static bool TryConsumeOwnershipQualifier(string type, string qualifier, out string remainingType)
        {
            remainingType = string.Empty;
            if (!type.StartsWith(qualifier, StringComparison.Ordinal))
            {
                return false;
            }

            if (type.Length == qualifier.Length || !char.IsWhiteSpace(type[qualifier.Length]))
            {
                return false;
            }

            remainingType = type[qualifier.Length..];
            return true;
        }

        private static string GetDeclaredTypeName(string type)
        {
            var trimmed = type.Trim();
            var genericStart = trimmed.IndexOf('<', StringComparison.Ordinal);
            return genericStart < 0 ? trimmed : trimmed[..genericStart].Trim();
        }

        private SourceLocation FindParameterTypeLocation(FunctionDeclaration function, ParameterDeclaration parameter)
        {
            var searchStart = function.Location.Offset;
            if (searchStart < 0 || searchStart >= _source.Length)
            {
                return function.Location;
            }

            var parameterMarker = $"{parameter.Name}:";
            var parameterOffset = _source.IndexOf(parameterMarker, searchStart, StringComparison.Ordinal);
            if (parameterOffset < 0)
            {
                return function.Location;
            }

            var typeOffset = _source.IndexOf(parameter.Type, parameterOffset + parameterMarker.Length, StringComparison.Ordinal);
            if (typeOffset < 0)
            {
                return function.Location;
            }

            return LocationFromOffset(typeOffset);
        }

        private SourceLocation LocationFromOffset(int offset)
        {
            var line = 1;
            var column = 1;

            for (var i = 0; i < offset && i < _source.Length; i++)
            {
                if (_source[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new SourceLocation(line, column, offset);
        }

        private Token Expect(TokenKind kind, string message)
        {
            if (At(kind))
            {
                return Advance();
            }

            Report("AI9999", message, Current.Location);
            return new Token(kind, string.Empty, Current.Location);
        }

        private bool At(TokenKind kind) => Current.Kind == kind;

        private Token Current => _tokens[Math.Min(_position, _tokens.Count - 1)];

        private Token Advance()
        {
            var token = Current;
            if (!At(TokenKind.EndOfFile))
            {
                _position++;
            }

            return token;
        }

        private void SkipNewLines()
        {
            while (At(TokenKind.NewLine))
            {
                Advance();
            }
        }

        private void ConsumeLineRemainder()
        {
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.NewLine))
            {
                Advance();
            }

            SkipNewLines();
        }

        private string ReadUntilLineEnd()
        {
            var start = Current.Location.Offset;
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.NewLine))
            {
                Advance();
            }

            var end = Current.Location.Offset;
            ConsumeLineRemainder();
            return StripInlineComment(_source[start..end]);
        }

        private void Report(string code, string message, SourceLocation location) =>
            _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, location));

        private static string StripInlineComment(string raw)
        {
            var idx = raw.IndexOf("//", StringComparison.Ordinal);
            return (idx >= 0 ? raw[..idx] : raw).Trim();
        }
    }
}
