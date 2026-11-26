namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static (ScalarParameterMode Mode, string Element) ParameterPassing(string text)
    {
        var spelling = ScopedReference(text) ? text.TrimStart()[7..] : text;
        var match = System.Text.RegularExpressions.Regex.Match(spelling.Trim(), @"^(ref\s+readonly|ref|out|in)\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return (ScalarParameterMode.Value, text);
        var mode = match.Groups[1].Value switch { "ref" => ScalarParameterMode.Ref, "out" => ScalarParameterMode.Out, "in" => ScalarParameterMode.In, _ => ScalarParameterMode.RefReadonly };
        return (mode, match.Groups[2].Value.Trim());
    }

    private static string ReferenceSyntax(ScalarParameterMode mode) => mode switch
    { ScalarParameterMode.Ref => "ref ", ScalarParameterMode.Out => "out ", ScalarParameterMode.In => "in ", ScalarParameterMode.RefReadonly => "ref readonly ", _ => "" };
    private static string ForwardSyntax(string type) => ParameterPassing(type).Mode switch
    { ScalarParameterMode.Ref => "ref ", ScalarParameterMode.Out => "out ", ScalarParameterMode.In or ScalarParameterMode.RefReadonly => "in ", _ => "" };
    private static bool CompatibleReferenceMethod(ScalarParameterMode method, ScalarParameterMode target)
        => method == target || method is ScalarParameterMode.In or ScalarParameterMode.RefReadonly
            && target is ScalarParameterMode.In or ScalarParameterMode.RefReadonly or ScalarParameterMode.Ref;

    private sealed partial class ExtendedBinder
    {
        private readonly Dictionary<string, ScalarParameterMode> _referenceParameters = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, ScalarType> _inferredOutVariables = new(System.StringComparer.Ordinal);
        private bool _outputStorage;
        private string? ReferenceStoragePath(ScalarExpression expression)
        {
            if (expression.Operation == "local") return expression.Value;
            if (expression.Operation == "field" && expression.Left is not null && !IsClass(expression.Left.Type)
                && ReferenceStoragePath(expression.Left) is { } root) return root + "." + expression.Value;
            return null;
        }
        private IEnumerable<string> ReferenceLeaves(string path, ScalarType type)
        {
            if (!IsStruct(type) || IsClass(type) || StructOf(type).IsTypeParameter) { yield return path; yield break; }
            var fields = StructOf(type).Fields;
            for (var i = 0; i < fields.Count; i++) foreach (var leaf in ReferenceLeaves(path + "." + i, fields[i].Type)) yield return leaf;
        }
        private bool AssignedStorage(ScalarExpression expression)
        {
            if (ReferenceStoragePath(expression) is not { } path) return true;
            for (var prefix = path; ;)
            {
                if (_assigned.Contains(prefix)) return true;
                var dot = prefix.LastIndexOf('.'); if (dot < 0) break; prefix = prefix[..dot];
            }
            return ReferenceLeaves(path, expression.Type).All(_assigned.Contains);
        }
        private bool OutputStructStorage(ScalarExpression expression)
        {
            var root = RootOf(expression);
            return root.Operation == "local" && IsStruct(root.Type) && !IsClass(root.Type)
                && _referenceParameters.GetValueOrDefault(root.Value!) == ScalarParameterMode.Out;
        }
        private void ReadNavigationReceiver(ScalarExpression expression)
        {
            if ((_outputStorage || OutputStructStorage(expression)) && IsStruct(expression.Type) && !IsClass(expression.Type)
                && Current.Text == "." && StructOf(expression.Type).Fields.Any(f => f.Name == TokenAt(_position + 1).Text && !f.IsProperty)) return;
            if (OutputStructStorage(expression) && expression.Operation == "field")
            {
                if (!AssignedStorage(expression)) Fail("AI3102", "Storage is read before assignment on every reaching path.", expression.Location);
                return;
            }
            Read(RootOf(expression));
        }
        private void AssignReferenceStorage(ScalarExpression expression)
        {
            if (ReferenceStoragePath(expression) is not { } path) return;
            _assigned.Add(path); _assigned.UnionWith(ReferenceLeaves(path, expression.Type));
            var root = RootOf(expression);
            if (root.Operation == "local" && AssignedStorage(root)) _assigned.Add(root.Value!);
        }
        private Lexeme ParameterTypeToken(bool declaration = false)
        {
            var start = Current;
            var scoped = Current.Text == "scoped" && TokenAt(_position + 1).Text is "ref" or "out" or "in" && Take("scoped");
            if (scoped && !Has(ScalarFeatures.ReferenceVariables)) Fail("AI3100", "Scoped references are disabled in this profile.", start.Location);
            if (Current.Text is not ("ref" or "out" or "in")) return declaration ? HeaderTypeToken() : TypeToken();
            if (!Has(ScalarFeatures.ByReferenceParameters)) Fail("AI3100", "By-reference parameters are disabled in this profile.", start.Location);
            var mode = Next().Text;
            if (mode == "ref" && Take("readonly")) mode = "ref readonly";
            var element = declaration ? HeaderTypeToken() : TypeToken();
            return new((scoped ? "scoped " : "") + mode + " " + element.Text, start.Location);
        }
        private static ScalarExpression ForwardArgument(ScalarExpression value, ScalarParameterMode mode) => mode == ScalarParameterMode.Value ? value
            : new(mode switch { ScalarParameterMode.Ref => "argument-ref", ScalarParameterMode.Out => "argument-out", _ => "argument-in" }, value.Type, value.Location, "forwarded", Left: value);
        private ScalarParameter SignatureParameter(ParameterDeclaration parameter, SourceLocation at)
            => new(Fresh(), Type(parameter.Type, at)) { Mode = ParameterPassing(parameter.Type).Mode, Scoped = ScopedReference(parameter.Type) };
        private static ScalarExpression ForwardParameter(ScalarParameter parameter, SourceLocation at)
            => ForwardArgument(new("local", parameter.Type, at, parameter.Name), parameter.Mode);
        private static bool ReferenceArgument(ScalarExpression argument) => argument.Operation is "argument-ref" or "argument-out" or "argument-in";
        private ScalarExpression CallArgument()
        {
            if(IsSourceIdentifier(Current.Text)&&TokenAt(_position+1).Text==":")
            {if(!Has(ScalarFeatures.NamedArguments))Fail("AI3100","Named arguments are disabled in this profile.",Current.Location);var name=Next();Next();var argument=CallArgument();return new("named-argument",argument.Type,name.Location,name.Name,Left:argument);}
            if (Current.Text is not ("ref" or "out" or "in")) { var value = Expression(); Read(value); return value; }
            var modifier = Next();
            if (!Has(ScalarFeatures.ByReferenceParameters)) Fail("AI3100", "By-reference arguments are disabled in this profile.", modifier.Location);
            if (modifier.Text == "out" && IsSourceIdentifier(Current.Text) && TokenAt(_position + 1).Text == ":")
            {
                var name = Next(); Next(); var syntax = TypeToken(); var type = Type(syntax.Text, syntax.Location);
                return DeclareOutputVariable(name.Text == "_" ? null : name.Text, name.Location, modifier.Location, type);
            }
            if (modifier.Text == "out" && Take("var"))
            {
                var name = Next();
                if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text)) Fail("AI3106", "Expected a name after 'out var'.", name.Location);
                return DeclareOutputVariable(name.Text == "_" ? null : name.Text, name.Location, modifier.Location);
            }
            if (modifier.Text == "out" && Current.Text == "_" && !IsLocalName("_"))
            { var discard = Next(); return DeclareOutputVariable(null, discard.Location, modifier.Location); }
            var previous = _outputStorage; ScalarExpression storage;
            try { _outputStorage = modifier.Text == "out"; storage = Expression(); }
            finally { _outputStorage = previous; }
            if (storage is { Operation: "local", Value: { } inferredName }
                && _inferredOutVariables.TryGetValue(inferredName, out var inferredType) && inferredType == ScalarType.Unit)
                Fail("AI3106", "An implicitly typed out variable cannot be referenced again before its argument list is bound.", storage.Location);
            if (modifier.Text != "out") Read(storage);
            return new("argument-" + modifier.Text, storage.Type, modifier.Location, Left: storage);
        }

        private ScalarExpression DeclareOutputVariable(string? name, SourceLocation at, SourceLocation modifier, ScalarType type = ScalarType.Unit)
        {
            var local = name is null ? HiddenLocal(type) : Declare(name, type, true, at);
            _inferredOutVariables.Add(local.EmittedName, type);
            _pendingDeclarations.Add(new ScalarStatement("local", at, Name: local.EmittedName, Type: type) { Mutable = true });
            return new("argument-out", type, modifier, name is null ? "discard" : null, Left: new("local", type, at, local.EmittedName));
        }

        private ScalarExpression ResolveOutputVariable(ScalarExpression argument, ScalarType expected)
        {
            if (argument.Left is not { Operation: "local", Value: { } name } || !_inferredOutVariables.TryGetValue(name, out var type)) return argument;
            if (type == ScalarType.Unit)
            {
                type = expected; _inferredOutVariables[name] = type;
                foreach (var scope in _scopes)
                    foreach (var key in scope.Where(p => p.Value.EmittedName == name).Select(p => p.Key).ToArray()) scope[key] = scope[key] with { Type = type };
                for (var i = 0; i < _pendingDeclarations.Count; i++)
                    if (_pendingDeclarations[i].Name == name) _pendingDeclarations[i] = _pendingDeclarations[i] with { Type = type };
            }
            return argument with { Type = type, Left = argument.Left with { Type = type } };
        }

        private bool IsReferenceStorage(ScalarExpression expression)
        {
            if (expression.Operation is "ref-select" or "ref-reassign" || expression.Operation == "call" && expression.ReferenceMode != ScalarParameterMode.Value) return true;
            if (_propertyReads.ContainsKey(expression)) return false;
            if (expression.Operation is "local" or "array-index") return true;
            if (expression.Operation == "static-field")
            {
                var parts = expression.Value!.Split(':');
                return !_structs[int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)].StaticFields[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)].IsProperty;
            }
            if (expression.Operation != "field") return false;
            for (var node = expression; node.Operation == "field"; node = node.Left!)
            {
                if (StructOf(node.Left!.Type).Fields[int.Parse(node.Value!, System.Globalization.CultureInfo.InvariantCulture)].IsProperty) return false;
                if (IsClass(node.Left.Type)) return true;
            }
            return IsReferenceStorage(RootOf(expression));
        }

        private ScalarExpression BindArgument(ScalarExpression argument, ScalarType type, ScalarParameterMode mode)
        {
            if (mode == ScalarParameterMode.Value)
            {
                if (ReferenceArgument(argument) || argument.Operation == "out-variable") Fail("AI3107", "This parameter is passed by value.", argument.Location);
                return Coerce(argument, type);
            }
            if (argument.Operation == "out-variable")
            {
                if (mode != ScalarParameterMode.Out) Fail("AI3107", "An 'out var' argument requires an out parameter.", argument.Location);
                var local = Declare(argument.Value!, type, true, argument.Location);
                _pendingDeclarations.Add(new ScalarStatement("local", argument.Location, Name: local.EmittedName, Type: type) { Mutable = true });
                argument = new("argument-out", type, argument.Location, Left: new("local", type, argument.Location, local.EmittedName));
            }
            var expected = mode switch { ScalarParameterMode.Ref => "argument-ref", ScalarParameterMode.Out => "argument-out", _ => "argument-in" };
            if (!ReferenceArgument(argument))
            {
                if (mode is ScalarParameterMode.Ref or ScalarParameterMode.Out) Fail("AI3107", "This parameter requires an explicit reference argument modifier.", argument.Location);
                var value = Coerce(argument, type);
                if (value.Type == argument.Type && ReferenceEquals(value, argument) && IsReferenceStorage(argument))
                    return new("argument-in", type, argument.Location, Left: argument);
                return new("argument-temporary", type, argument.Location, Left: value);
            }
            if (argument.Operation != expected && !(mode is ScalarParameterMode.In or ScalarParameterMode.RefReadonly && argument.Operation == "argument-ref"))
                Fail("AI3107", "Argument and parameter reference modifiers do not match.", argument.Location);
            argument = ResolveOutputVariable(argument, type);
            var storage = argument.Left!;
            if (storage.Type != type) Fail("AI3107", "A by-reference argument requires an identical element type.", storage.Location);
            if (!IsReferenceStorage(storage)) Fail("AI3105", "A by-reference argument requires variable storage; properties are values.", storage.Location);
            if (argument.Value is "forwarded" or "discard") return argument;
            if (mode is ScalarParameterMode.Ref or ScalarParameterMode.Out || argument.Operation == "argument-ref") Write(storage);
            else Read(storage);
            return argument;
        }

        private void CompleteArguments(IEnumerable<ScalarExpression> arguments)
        {
            foreach (var argument in arguments)
                if (argument is { Operation: "argument-out", Left: not null }) AssignReferenceStorage(argument.Left);
        }
    }
}
