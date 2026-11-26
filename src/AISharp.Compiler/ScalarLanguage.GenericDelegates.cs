namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record DelegateTypeParameter(string Name, int Variance, SourceLocation Location);
    private sealed record DelegateSourceMap(int Start, int Length, SourceLocation Original, bool Relative);
    [ThreadStatic] private static HashSet<int>? delegateHelperOffsets;
    [ThreadStatic] private static List<DelegateSourceMap>? delegateSourceMaps;
    private static readonly string[] DelegateOperations = ["invoke", "append", "combine", "count", "at", "prefix", "splice", "remove", "equals"];

    private static DelegateInfo? DelegateDeclaration(string name, out bool function)
    {
        function = false;
        if (activeDelegates!.TryGetValue(name, out var direct)) return direct;
        if (name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("_fn", StringComparison.Ordinal) && activeDelegates.TryGetValue(name[2..^3], out var owner))
        { function = true; return owner; }
        return null;
    }

    private static Diagnostic MapDelegateDiagnostic(Diagnostic diagnostic, string source)
    {
        var map = delegateSourceMaps!.FirstOrDefault(m => diagnostic.Location.Offset >= m.Start && diagnostic.Location.Offset < m.Start + m.Length);
        return map is null ? diagnostic : diagnostic with { Location = map.Relative ? LocationAt(source, map.Original.Offset + diagnostic.Location.Offset - map.Start) : map.Original };
    }

    private static string GenericDelegateText(DelegateInfo d, int start)
    {
        var parameters = "<" + string.Join(", ", d.TypeParameters.Select(p => TypeIdentifierSyntax(p.Name))) + ">";
        var node = TypeIdentifierSyntax(d.Name) + parameters; var callable = "__" + d.Name + "_fn" + parameters;
        var typed = string.Join(", ", d.Parameters.Select((p, i) => $"p{i}: {p.Type}"));
        var args = string.Join(", ", d.Parameters.Select((p, i) => ForwardSyntax(p.Type) + "p" + i)); var more = typed.Length == 0 ? "" : ", " + typed;
        var resultMode = ParameterPassing(d.Return).Mode; var resultElement = ParameterPassing(d.Return).Element;
        var unit = d.Return is "void" or "unit"; var text = new System.Text.StringBuilder();
        string H(string operation) => "__" + d.Name + "_" + operation + parameters;
        void Header(string prefix)
        {
            var at = start + text.Length; text.Append(prefix);
            var open = prefix.IndexOf('<'); var cursor = open + 1;
            foreach (var parameter in d.TypeParameters)
            {
                var position = prefix.IndexOf(parameter.Name, cursor, StringComparison.Ordinal);
                delegateSourceMaps!.Add(new(at + position, parameter.Name.Length, parameter.Location, false)); cursor = position + parameter.Name.Length;
            }
            if (d.WhereClause.Length > 0)
            {
                text.Append(' '); var where = start + text.Length; text.Append(d.WhereClause.Replace('\r', ' ').Replace('\n', ' '));
                delegateSourceMaps!.Add(new(where, d.WhereClause.Length, d.WhereLocation, true));
            }
            text.Append('\n');
        }
        void F(string operation, string signature, params string[] body)
        {
            Header("fn " + H(operation) + signature); text.Append("    ensures true\n{\n");
            foreach (var line in body) text.Append("    ").Append(line).Append('\n');
            text.Append("}\n");
        }
        text.Append('\n'); Header("abstract class " + callable); text.Append("{\n");
        text.Append($"    public abstract fn Run({typed}) -> {d.Return}\n    public abstract fn Same(other: object) -> bool\n}}\n");
        Header("sealed class " + node); text.Append($"{{\n    public h: {callable}\n    public t: {node}\n}}\n");
        if (unit) F("invoke", $"(d: {node}{more}) -> void", $"d.h.Run({args})", "var n = d.t", "while (n != null) {", $"    n.h.Run({args})", "    n = n.t", "}");
        else if (resultMode != ScalarParameterMode.Value) F("invoke", $"(d: {node}{more}) -> {d.Return}", $"{ReferenceSyntax(resultMode)}{resultElement} r = ref d.h.Run({args})", "var n = d.t", "while (n != null) {", $"    r = ref n.h.Run({args})", "    n = n.t", "}", "return ref r");
        else F("invoke", $"(d: {node}{more}) -> {d.Return}", $"var r = d.h.Run({args})", "var n = d.t", "while (n != null) {", $"    r = n.h.Run({args})", "    n = n.t", "}", "return r");
        F("append", $"(a: {node}, b: {node}) -> {node}", "if (a.t == null) {", $"    return new {node} {{ h = a.h, t = b }}", "}", $"return new {node} {{ h = a.h, t = {H("append")}(a.t, b) }}");
        F("combine", $"(a: {node}, b: {node}) -> {node}", "if (a == null) {", "    return b", "}", "if (b == null) {", "    return a", "}", $"return {H("append")}(a, b)");
        F("count", $"(d: {node}) -> int", "var k = 0", "var n = d", "while (n != null) {", "    k = k + 1", "    n = n.t", "}", "return k");
        F("at", $"(d: {node}, i: int) -> {node}", "var n = d", "var k = i", "while (k > 0) {", "    n = n.t", "    k = k - 1", "}", "return n");
        F("prefix", $"(s: {node}, v: {node}) -> bool", "var p = s", "var q = v", "while (q != null) {", "    if (!p.h.Same(q.h)) {", "        return false", "    }", "    p = p.t", "    q = q.t", "}", "return true");
        F("splice", $"(s: {node}, i: int, m: int) -> {node}", "if (i == 0) {", $"    return {H("at")}(s, m)", "}", $"return new {node} {{ h = s.h, t = {H("splice")}(s.t, i - 1, m) }}");
        F("remove", $"(s: {node}, v: {node}) -> {node}", "if (s == null) {", "    return null", "}", "if (v == null) {", "    return s", "}", $"let c = {H("count")}(s)", $"let m = {H("count")}(v)", "var i = c - m", "while (i >= 0) {", $"    if ({H("prefix")}({H("at")}(s, i), v)) {{", $"        return {H("splice")}(s, i, m)", "    }", "    i = i - 1", "}", "return s");
        F("equals", $"(a: {node}, b: {node}) -> bool", "if (a == null) {", "    return b == null", "}", "if (b == null) {", "    return false", "}", "var p = a", "var q = b", "while (p != null && q != null) {", "    if (!p.h.Same(q.h)) {", "        return false", "    }", "    p = p.t", "    q = q.t", "}", "return p == null && q == null");
        return text.ToString();
    }

    private sealed partial class ExtendedBinder
    {
        public DelegateInfo ParseDelegateDeclaration()
        {
            Lexeme SignatureType()
            {
                var at = Current.Location;
                try { return ParameterTypeToken(true); }
                catch (ScalarError failure) when (failure.Diagnostic.Code == "AI3106")
                { throw DeclarationError("AI3106", failure.Diagnostic.Message, at); }
            }
            Expect("delegate"); Expect("fn"); var name = Next();
            if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || IsTypeKeyword(name.Text)) Fail("AI3106", "Invalid delegate name.", name.Location);
            var typeParameters = new List<DelegateTypeParameter>();
            if (Take("<"))
            {
                if (!Has(ScalarFeatures.Generics)) Fail("AI3100", "Generic delegates are disabled.", name.Location);
                do
                {
                    var parameter = Next(); var variance = 0;
                    if (parameter.Text is "in" or "out") { variance = parameter.Text == "out" ? 1 : -1; parameter = Next(); }
                    if (!ValidTypeParameterName(parameter.Text))
                        Fail("AI3106", "Invalid delegate type parameter.", parameter.Location);
                    if (parameter.Name == name.Name || typeParameters.Any(p => p.Name == parameter.Name)) Fail("AI3106", "Duplicate delegate type parameter.", parameter.Location);
                    typeParameters.Add(new(parameter.Name, variance, parameter.Location));
                } while (Take(","));
                Expect(">");
            }
            var parameters = new List<(string Name, string Type)>(); var locations = new List<(string Type, SourceLocation Location)>();
            Expect("(");
            if (!Take(")"))
            {
                do
                {
                    var parameter = Next();
                    if (!IsSourceIdentifier(parameter.Text) || IsReserved(parameter.Text)) Fail("AI3106", "Invalid delegate parameter name.", parameter.Location);
                    if (parameters.Any(p => p.Name == parameter.Name)) Fail("AI3106", "Duplicate delegate parameter.", parameter.Location);
                    Expect(":"); var type = SignatureType(); var passing = ParameterPassing(type.Text);
                    var text = (ScopedReference(type.Text) ? "scoped " : "") + ReferenceSyntax(passing.Mode) + System.Text.RegularExpressions.Regex.Replace(passing.Element, @"\s+", "");
                    parameters.Add((parameter.Name, text)); locations.Add((text, type.Location));
                } while (Take(","));
                Expect(")");
            }
            Expect("-"); Expect(">"); var result = SignatureType(); var resultPassing = ParameterPassing(result.Text); var resultType = ReferenceSyntax(resultPassing.Mode) + System.Text.RegularExpressions.Regex.Replace(resultPassing.Element, @"\s+", "");
            if (resultPassing.Mode != ScalarParameterMode.Value && !Has(ScalarFeatures.ReferenceReturns)) Fail("AI3100", "Reference-returning delegates are disabled in this profile.", result.Location);
            if (resultPassing.Mode is ScalarParameterMode.In or ScalarParameterMode.Out || ScopedReference(result.Text)) Fail("AI3106", "A delegate return uses ref or ref readonly.", result.Location);
            NewLines(); var where = ""; var whereLocation = name.Location;
            if (Current.Text == "where")
            {
                if (typeParameters.Count == 0) Fail("AI3106", "A non-generic delegate has no constraint parameters.", Current.Location);
                whereLocation = Current.Location;
                var terminator=_tokens.LastOrDefault(t=>t.Text is not ("" or "\n" or "\r\n"));
                var whereEnd=terminator is {Text:";"}?terminator.Location.Offset-_declarationStart.Offset:_declaration!.Length;
                where = _declaration![(whereLocation.Offset - _declarationStart.Offset)..whereEnd].TrimEnd();
                while (Current.Text != "") Next();
            }
            if(Take(";"))NewLines();
            if (Current.Text != "") Fail("AI3106", "Unexpected token after the delegate declaration.", Current.Location);
            return new(name.Name, parameters, resultType, name.Location)
            {
                EscapedName = IsEscapedIdentifier(name.Text), TypeParameters = typeParameters, WhereClause = where, WhereLocation = whereLocation, ReturnLocation = result.Location,
                SignatureLocations = resultType is "void" or "unit" ? locations : [.. locations, (resultType, result.Location)]
            };
        }

        private DelegateInfo ClosedDelegateInfo(ScalarType type)
        {
            var owner = StructOf(type); var definition = activeDelegates![owner.Template < 0 ? owner.Name : _structs[owner.Template].Name];
            if (definition.TypeParameters.Count == 0) return definition;
            var map = owner.TypeArguments!;
            return definition with { Name = owner.Name, TypeArguments = owner.TypeArgumentList,
                Parameters = definition.Parameters.Select(p => (p.Name, SubstituteSignatureTypeText(p.Type, map))).ToArray(), Return = SubstituteSignatureTypeText(definition.Return, map) };
        }

        private (Lexeme Token, ScalarType Type, int End)? GenericDelegateCast()
        {
            var written=TokenAt(_position+1).Text;var named=StructNamedAny(written);
            var builtin=Has(ScalarFeatures.Boxing)&&(written=="IEquatable"||BuiltinComparerTypeName(written)||BuiltinSequenceTypeName(written))&&named is null;
            if ((!Delegating&&!Has(ScalarFeatures.Boxing)) || !Has(ScalarFeatures.Generics) || Current.Text != "("
                || !builtin&&(named is not {TypeParameters.Count:>0} || !named.IsDelegate&&!Has(ScalarFeatures.Boxing))) return null;
            var saved = _position;var tokens=_tokens;_tokens=[..tokens];
            try
            {
                Next(); var token = TypeToken();
                if (!Take(")")) return null;
                return (token, Type(token.Text, token.Location), _position);
            }
            finally { _position = saved;_tokens=tokens; }
        }

        private ScalarType DelegateSameType(DelegateInfo info) => info.TypeParameters.Count == 0 ? _structs[FunctionClassOf(info)].Id : (ScalarType)ObjectTypeId;

        private ScalarExpression DelegateHelperCall(DelegateInfo info, string operation, List<ScalarExpression> arguments, SourceLocation at)
        {
            var name = "__" + info.DefinitionName + "_" + operation;
            if (info.TypeParameters.Count == 0) return ReferenceResult(new("call", operation == "invoke" ? Type(info.Return, at, true) : operation is "equals" or "prefix" ? ScalarType.Bool : operation == "count" ? ScalarType.Int : arguments[0].Type, at, name) { Arguments = arguments }, operation == "invoke" ? info.Return : "void", operation == "invoke" ? info.Parameters.Select(p=>new ParameterDeclaration(p.Name,p.Type)).ToList() : null, 1);
            var declaration = _functions.FirstOrDefault(f => f.Name == name);
            if (declaration is null) { Fail("AI3106", "The generic delegate helper is not bound.", at); return null!; }
            return GenericCall(new(name, at), declaration, GenericOf(declaration)!, info.TypeArguments.Select(t => (t, at)).ToList(), arguments);
        }
    }
}
