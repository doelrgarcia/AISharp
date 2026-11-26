namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static bool ScopedReference(string text) => text.TrimStart().StartsWith("scoped ", System.StringComparison.Ordinal);
    private static ScalarExpression ReferenceResult(ScalarExpression expression, string result, IReadOnlyList<ParameterDeclaration>? parameters = null, int receiver = 0)
        => expression with { ReferenceMode = ParameterPassing(result).Mode, ReferenceExcludedArguments = parameters is null ? null : parameters.Select((p,i)=>(p,i)).Where(x=>ScopedReference(x.p.Type)).Select(x=>x.i+receiver).ToArray() };
    private static ScalarStatement Returning(ScalarExpression value, ScalarParameterMode mode, SourceLocation at)
        => new("return", at, mode == ScalarParameterMode.Value ? value : new("reference-return", value.Type, at, Left: value));
    private static void CheckReferenceScopeContract(ScalarStructMethod implementation, ScalarStructMethod root)
    {
        if (ParameterPassing(root.Declaration.ReturnType).Mode == ScalarParameterMode.Value) return;
        if (implementation.Declaration.Parameters.Zip(root.Declaration.Parameters).Any(p=>ScopedReference(p.Second.Type) && !ScopedReference(p.First.Type)))
            throw DeclarationError("AI3106", "A reference-returning implementation cannot weaken the inherited scoped parameter contract.", implementation.Declaration.Location);
    }

    private sealed partial class ExtendedBinder
    {
        private readonly Dictionary<string, int> _referenceContexts = new(System.StringComparer.Ordinal);
        private readonly HashSet<string> _referenceLocals = new(System.StringComparer.Ordinal);
        private ScalarParameterMode _returnMode;
        private int ReferenceContext(ScalarExpression value)
        {
            if (value.Operation == "local") return _referenceContexts.GetValueOrDefault(value.Value!, _scopes.Count + 1);
            if (value.Operation is "static-field" or "array-index") return 0;
            if (value is { Operation: "field", Left: not null }) return IsClass(value.Left.Type) ? 0 : ReferenceContext(value.Left);
            if (value is { Operation: "ref-select", Arguments: { } arms }) return arms.Max(ReferenceContext);
            if (value.Operation == "ref-reassign") return ReferenceContext(value.Left!);
            if (value.Operation == "call" && value.ReferenceMode != ScalarParameterMode.Value)
            {
                var contexts = (value.Arguments ?? []).Where((a,i)=>value.ReferenceExcludedArguments?.Contains(i)!=true)
                    .Where(a => ReferenceArgument(a) || a.Operation == "argument-temporary")
                    .Where(a => a.Operation != "argument-out").Select(a => a.Left is null || a.Operation == "argument-temporary" ? _scopes.Count + 1 : ReferenceContext(a.Left));
                return contexts.DefaultIfEmpty(0).Max();
            }
            return _scopes.Count + 1;
        }
        private ScalarExpression ReferenceTarget()
        {
            var value = Expression();
            if (!IsReferenceStorage(value)) Fail("AI3105", "A reference requires variable storage, not a temporary or property value.", value.Location);
            Read(value); return value;
        }
        private ScalarExpression ReferenceConditional(ScalarExpression condition)
        {
            if (!Has(ScalarFeatures.ReferenceVariables)) Fail("AI3100", "Reference conditionals are disabled in this profile.", Current.Location);
            var at = Next().Location; Require(condition, ScalarType.Bool); var before = new HashSet<string>(_assigned);
            Expect("ref"); var yes = ReferenceTarget(); var yesAssigned = new HashSet<string>(_assigned); _assigned = new(before);
            Expect(":"); Expect("ref"); var no = ReferenceTarget();
            if (yes.Type != no.Type) Fail("AI3107", "A reference conditional requires identical element types.", no.Location);
            if (BooleanConstant(condition) == true) _assigned = yesAssigned; else if (BooleanConstant(condition) != false) _assigned.IntersectWith(yesAssigned);
            return new("ref-select", yes.Type, at, Left: condition) { Arguments = [yes, no] };
        }
        private ScalarStatement ReferenceDeclaration(Lexeme keyword, Lexeme name, ScalarType? type, ScalarParameterMode mode, bool scoped)
        {
            if (!Has(ScalarFeatures.ReferenceVariables)) Fail("AI3100", "Reference locals are disabled in this profile.", keyword.Location);
            if (mode is not (ScalarParameterMode.Ref or ScalarParameterMode.RefReadonly)) Fail("AI3106", "A reference local uses ref or ref readonly.", keyword.Location);
            Expect("="); Expect("ref"); var source = ReferenceTarget();
            if (type is not null && type != source.Type) Fail("AI3107", "A reference initializer requires an identical element type.", source.Location);
            if (mode == ScalarParameterMode.Ref) Write(source);
            var local = Declare(name.Text, source.Type, mode == ScalarParameterMode.Ref, name.Location);
            _assigned.Add(local.EmittedName); _referenceLocals.Add(local.EmittedName); _referenceParameters[local.EmittedName] = mode;
            _referenceContexts[local.EmittedName] = scoped ? Math.Max(_scopes.Count + 1, ReferenceContext(source)) : ReferenceContext(source);
            return new("ref-local", keyword.Location, source, local.EmittedName, source.Type) { ReferenceMode = mode, Mutable = mode == ScalarParameterMode.Ref, Scoped = scoped };
        }
        private ScalarStatement PrefixReferenceDeclaration()
        {
            var at = Current; var scoped = Take("scoped"); Expect("ref"); var mode = Take("readonly") ? ScalarParameterMode.RefReadonly : ScalarParameterMode.Ref;
            ScalarType? type = null;
            if (!Take("var")) { var token = TypeToken(); type = Type(token.Text, token.Location); }
            var name = Next(); return ReferenceDeclaration(at, name, type, mode, scoped);
        }
        private ScalarExpression ReferenceReassignment(ScalarExpression target, ScalarExpression value, SourceLocation at)
        {
            if (!Has(ScalarFeatures.ReferenceVariables)) Fail("AI3100", "Reference reassignment is disabled in this profile.", at);
            if (_this is not null && target.Operation == "local" && target.Value == _this.EmittedName) Fail("AI3105", "The implicit this binding cannot be ref-reassigned.", target.Location);
            var mode = _referenceParameters.GetValueOrDefault(target.Value ?? "");
            if (target.Operation != "local" || mode == ScalarParameterMode.Value)
                Fail("AI3105", "Only a reference binding can be ref-reassigned.", target.Location);
            if (target.Type != value.Type) Fail("AI3107", "A reference reassignment requires identical element types.", value.Location);
            if (mode == ScalarParameterMode.Out) Read(target);
            if (mode is ScalarParameterMode.Ref or ScalarParameterMode.Out) Write(value);
            if (ReferenceContext(value) > ReferenceContext(target)) Fail("AI3106", "A reference cannot be reassigned to storage with a narrower lifetime.", value.Location);
            return new("ref-reassign", target.Type, at, Left: target, Right: value);
        }
        private ScalarStatement ReferenceReturn(SourceLocation at)
        {
            if (_returnMode == ScalarParameterMode.Value || !Has(ScalarFeatures.ReferenceReturns)) Fail("AI3107", "A ref return requires a reference-returning function.", at);
            var value = ReferenceTarget();
            if (value.Type != _returnType) Fail("AI3107", "A reference return requires an identical element type.", value.Location);
            if (_returnMode == ScalarParameterMode.Ref) Write(value);
            if (_receiverMode != ScalarParameterMode.Value && ReferenceContext(value) > 1 && RootOf(value).Value == _this?.EmittedName) Fail("AI3105", "A struct accessor cannot return its own instance storage by reference.", value.Location);
            if (ReferenceContext(value) > 1) Fail("AI3106", "A returned reference cannot escape local, value-parameter, out or scoped storage.", value.Location);
            EndStatement(); return Returning(value, _returnMode, at);
        }
    }
}
