namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<ScalarSource, SourceLocation>? accessorPrefixes;
    private static bool SameIndexSignature(IReadOnlyList<ParameterDeclaration> a, IReadOnlyList<ParameterDeclaration> b)
        => a.Count == b.Count && a.Zip(b).All(p => IndexParameterIdentity(p.First.Type) == IndexParameterIdentity(p.Second.Type));
    private static string IndexParameterIdentity(string type)
    {
        var passing = ParameterPassing(type);
        return (passing.Mode is ScalarParameterMode.In or ScalarParameterMode.RefReadonly ? "in " : passing.Mode == ScalarParameterMode.Value ? "" : passing.Mode + " ") + SignatureType(passing.Element);
    }
    private static bool SameAccessor(ScalarStructMethod candidate, ScalarStructMethod root)
        => candidate.IsAccessor && candidate.IsInitAccessor == root.IsInitAccessor && (root.AccessorMember is null ? candidate.AccessorMember is null && candidate.Name == root.Name : candidate.AccessorMember == root.AccessorMember)
            && candidate.Declaration.Parameters.Count == root.Declaration.Parameters.Count
            && candidate.Declaration.Parameters.Zip(root.Declaration.Parameters).All(p => MethodSignatureType(candidate.Declaration,p.First.Type) == MethodSignatureType(root.Declaration,p.Second.Type));
    private static ScalarStructMethod? FindAccessor(int type, ScalarStructMethod root)
    {
        for (var k = type; k >= 0; k = activeStructs![k].BaseIndex)
            if (activeStructs![k].Methods.FirstOrDefault(m => SameAccessor(m, root)) is { } found) return found;
        return null;
    }

    private sealed partial class ExtendedBinder
    {
        private readonly HashSet<ScalarExpression> _baseAccessorReceivers = new(ReferenceEqualityComparer.Instance);
        private List<ParameterDeclaration> BindIndexParameters(Lexeme name, bool isStatic, bool required)
        {
            if (!Has(ScalarFeatures.Indexers)) Fail("AI3100", "Indexers are disabled in this profile.", name.Location);
            if (isStatic || required) Fail("AI3106", "An indexer is an instance member and cannot be required.", name.Location);
            Expect("["); var parameters = new List<ParameterDeclaration>();
            if (Current.Text == "]") Fail("AI3106", "An indexer requires at least one parameter.", Current.Location);
            do
            {
                var parameter = Next();
                if (!IsSourceIdentifier(parameter.Text) || IsReserved(parameter.Text) || parameters.Any(p => IdentifierName(p.Name) == parameter.Name))
                    Fail("AI3106", "Invalid or duplicate indexer parameter.", parameter.Location);
                Expect(":"); var type = ParameterTypeToken();
                var mode = ParameterPassing(type.Text).Mode;
                if (mode is ScalarParameterMode.Ref or ScalarParameterMode.Out || mode == ScalarParameterMode.Value && ScopedReference(type.Text))
                    Fail("AI3106", "An index parameter uses a value, in or ref readonly declaration.", type.Location);
                Type(type.Text, type.Location); parameters.Add(new(parameter.Text, type.Text));
            } while (Take(","));
            Expect("]"); return parameters;
        }

        private sealed record AccessorBody(string Text, SourceLocation Location, SourceLocation? Prefix = null);
        private AccessorBody ReadAccessorBody(bool returns, Lexeme keyword, out bool usesField)
        {
            usesField = false;
            if (Current.Text == "{")
            {
                var brace = Next(); var start = brace.Location.Offset + 1; var depth = 1; Lexeme? close = null;
                while (Current.Text != "")
                {
                    var token = Next(); usesField |= token.Text == "field";
                    if (token.Text == "{") depth++;
                    else if (token.Text == "}" && --depth == 0) { close = token; break; }
                }
                if (close is null) Fail("AI3106", "Unterminated accessor body.", brace.Location);
                return new(_declaration![(start - _declarationStart.Offset)..(close!.Location.Offset - _declarationStart.Offset)],
                    new(brace.Location.Line, brace.Location.Column + 1, start));
            }
            var arrow = Current; Expect("=>"); var first = Current; var depthParen = 0; var depthBracket = 0; var depthBrace = 0;
            while (Current.Text != "" && !(Current.Text == ";" && depthParen == 0 && depthBracket == 0 && depthBrace == 0))
            {
                var token = Next(); usesField |= token.Text == "field";
                if (token.Text == "(") depthParen++; else if (token.Text == ")") depthParen--;
                if (token.Text == "[") depthBracket++; else if (token.Text == "]") depthBracket--;
                if (token.Text == "{") depthBrace++; else if (token.Text == "}") depthBrace--;
                if (depthParen < 0 || depthBracket < 0 || depthBrace < 0) Fail("AI3106", "Expected ';' after the accessor expression.", token.Location);
            }
            var end = Current; Expect(";");
            if (end.Location.Offset == first.Location.Offset) Fail("AI3106", "An accessor expression is required.", arrow.Location);
            var text = _declaration![(first.Location.Offset - _declarationStart.Offset)..(end.Location.Offset - _declarationStart.Offset)] + ";";
            if (!returns) return new(text, first.Location);
            var location = new SourceLocation(first.Location.Line, first.Location.Column - 7, first.Location.Offset - 7);
            return new("return " + text, location, keyword.Location);
        }

        private void BindProperty(ScalarStruct header, Lexeme name, Lexeme typeToken, ScalarType type, bool isPublic,
            List<ScalarStructField> fields, List<MemberSpan> methods, List<ScalarProperty> properties,
            bool isRequired = false, bool isStatic = false, IReadOnlyList<ParameterDeclaration>? indexParameters = null, IReadOnlySet<string>? memberModifiers = null, string? qualifier = null,bool isInternal=false)
        {
            indexParameters ??= []; memberModifiers ??= new HashSet<string>();
            var isIndexer = indexParameters.Count > 0; var referenceMode = ParameterPassing(typeToken.Text).Mode;
            var abstractMember = memberModifiers.Contains("abstract");
            Lexeme? get = null, set = null; AccessorBody? getBody = null, setBody = null;
            var setPrivate = false; var getPrivate = false; var isInit = false; var usesField = false; var readonlyAccessors = new HashSet<string>();
            if (Current.Text == "=>")
            {
                get = name; getBody = ReadAccessorBody(true, name, out usesField);
            }
            else
            {
                var open = Current; Expect("{"); NewLines();
                while (!Take("}"))
                {
                    if (Current.Text == "") Fail("AI3106", "Unterminated property.", open.Location);
                    var accessorPrivate = false; var accessorReadonly = Take("readonly");
                    if (Current.Text is "private" or "public" or "internal" or "protected")
                    {
                        var modifier = Next();
                        if (modifier.Text != "private") Fail("AI3100", "Only a private set or init accessor modifier is supported.", modifier.Location);
                        accessorPrivate = true;
                    }
                    if (!accessorReadonly) accessorReadonly = Take("readonly");
                    if (accessorReadonly && (header.IsClass || isStatic || memberModifiers.Contains("readonly"))) Fail("AI3106", "Invalid readonly accessor modifier.", Current.Location);
                    var keyword = Next();
                    if (keyword.Text is not ("get" or "set" or "init")) Fail("AI3106", "Expected get, set or init.", keyword.Location);
                    if (accessorReadonly && keyword.Text == "init") Fail("AI3106", "Readonly applies to the property declaration, not its init accessor.", keyword.Location);
                    if (keyword.Text == "get" ? get is not null : set is not null) Fail("AI3106", "Duplicate accessor.", keyword.Location);
                    if (keyword.Text == "get" && accessorPrivate && !Has(ScalarFeatures.ExtendedAccessors)) Fail("AI3100", "Private getters are disabled in this profile.", keyword.Location);
                    if (accessorPrivate && (qualifier is not null || abstractMember)) Fail("AI3106", "An explicit or abstract accessor cannot have a private accessor modifier.",keyword.Location);
                    if (accessorPrivate && !isPublic) Fail("AI3106", "An accessor modifier must be more restrictive than its property.", keyword.Location);
                    if (isStatic && keyword.Text == "init") Fail("AI3106", "This member cannot have an init accessor.", keyword.Location);
                    AccessorBody? body = null;
                    if (Current.Text is "{" or "=>") { body = ReadAccessorBody(keyword.Text == "get", keyword, out var field); usesField |= field; }
                    else if (!Take(";") && Current.Text is not "\n" and not "}") Fail("AI3106", "Expected ';' or an accessor body.");
                    if (accessorReadonly) readonlyAccessors.Add(keyword.Text == "get" ? "get" : "set");
                    if (keyword.Text == "get") { get = keyword; getBody = body; getPrivate = accessorPrivate; }
                    else { set = keyword; setBody = body; setPrivate = accessorPrivate; isInit = keyword.Text == "init"; }
                    NewLines();
                }
            }
            if (get is null && set is null) Fail("AI3106", "A property requires an accessor.", name.Location);
            if(header.IsReadonly&&!isStatic&&set is not null&&!isInit&&setBody is null)Fail("AI3106","An automatic property in a readonly struct must be get-only or init-only.",set.Location);
            if (getPrivate && setPrivate || (getPrivate || setPrivate) && (get is null || set is null)) Fail("AI3106", "Only one accessor of a two-accessor property may have a private modifier.",name.Location);
            if (readonlyAccessors.Count > 1) Fail("AI3106", "Readonly cannot be specified on both accessors.", name.Location);
            if (readonlyAccessors.Count > 0 && (get is null || set is null)) Fail("AI3106", "A single-accessor readonly member places readonly on the property declaration.", name.Location);
            if (set is not null && indexParameters.Any(p => p.Name == "value")) Fail("AI3106", "A writable indexer cannot name an index parameter 'value'.", name.Location);
            if (referenceMode != ScalarParameterMode.Value && set is not null) Fail("AI3106", "A reference-returning property cannot have a set or init accessor.", set.Location);
            if (header.IsInterface && (!isStatic || abstractMember || memberModifiers.Contains("virtual")))
            {
                if (get is not null) CheckVarianceSafety(header,typeToken.Text,1,typeToken.Location);
                if (set is not null) CheckVarianceSafety(header,typeToken.Text,-1,typeToken.Location);
                foreach (var parameter in indexParameters) CheckVarianceSafety(header,parameter.Type,-1,name.Location);
            }
            if (abstractMember && (getBody is not null || setBody is not null)) Fail("AI3106", "An abstract accessor cannot have a body.", name.Location);
            var signatureOnly = abstractMember || header.IsInterface && !isStatic && qualifier is null && getBody is null && setBody is null;
            if (header.IsInterface && !isStatic && (getPrivate && getBody is null || setPrivate && setBody is null)) Fail("AI3106", "A private interface accessor requires a body.",name.Location);
            if (header.IsInterface && (!isPublic && (signatureOnly || memberModifiers.Any(m => m is "virtual" or "sealed")) || memberModifiers.Contains("virtual") && memberModifiers.Contains("sealed")))
                Fail("AI3106","Invalid interface property modifiers.",name.Location);
            if (header.IsInterface && isStatic && !abstractMember && referenceMode != ScalarParameterMode.Value && getBody is null) Fail("AI3106","A reference-valued static property requires a getter body.",name.Location);
            var declarationAccessors = abstractMember || header.IsInterface && !isStatic && qualifier is null;
            if (referenceMode != ScalarParameterMode.Value && getBody is null && !declarationAccessors) Fail("AI3106", "A reference-returning property requires a getter body.", name.Location);
            if (isRequired && (set is null || setPrivate)) Fail("AI3106", "A required property needs an accessible setter.", name.Location);
            if (qualifier is not null && header.IsInterface && getBody is null && setBody is null) Fail("AI3106", "An explicit interface accessor requires an implementation body.",name.Location);
            if (isIndexer && !declarationAccessors && (get is not null && getBody is null || set is not null && setBody is null)) Fail("AI3106", "An indexer accessor requires a body.", name.Location);
            var extendedAutomatic = !signatureOnly && !isIndexer && getBody is null && setBody is null
                && (qualifier is not null || getPrivate || !header.IsInterface && memberModifiers.Any(m => m is "virtual" or "override" or "sealed"));
            if (extendedAutomatic)
            {
                if (!Has(ScalarFeatures.ExtendedAccessors)) Fail("AI3100", "Extended automatic accessors are disabled in this profile.",name.Location);
                if (get is null) Fail("AI3106", "An automatic property requires a getter.",name.Location);
                if (set is not null && !isInit && (memberModifiers.Contains("readonly") || readonlyAccessors.Contains("set"))) Fail("AI3106", "An automatic set accessor cannot be readonly.",set.Location);
                usesField = true;
                getBody = new("return field",get!.Location);
                if (set is not null) setBody = new("field = value",set.Location);
            }
            var virtualAutomatic = header.IsInterface && isStatic && !abstractMember && memberModifiers.Contains("virtual") && getBody is null && setBody is null;
            if (virtualAutomatic)
            {
                usesField = true;
                if (get is not null) getBody = new("return field",get.Location);
                if (set is not null) setBody = new("field = value",set.Location);
            }
            var storage = EraseType(type);
            if (!signatureOnly && !isIndexer && getBody is null && setBody is null)
            {
                if (get is null) Fail("AI3106", "An auto-property requires a get accessor.", set!.Location);
                if (set is not null && !isInit && (memberModifiers.Contains("readonly") || readonlyAccessors.Contains("set"))) Fail("AI3106", "An automatic set accessor cannot be readonly.", set.Location);
                if (memberModifiers.Any(m => m is "virtual" or "override" or "sealed")) Fail("AI3100", "Virtual automatic properties require explicit accessor bodies in this profile.", name.Location);
                fields.Add(new(name.Text, type, storage, isPublic, set is null || header.IsReadonly&&!isStatic, name.Location) { IsInternal=isInternal,IsInit = isInit, WritePrivate = setPrivate, IsProperty = true, IsRequired = isRequired, Owner = isStatic ? IndexOf(header.Id) : -1 });
                return;
            }
            if (!isStatic && !header.IsClass && setBody is not null && !Has(ScalarFeatures.MutableStructAccessors)) Fail("AI3100", "Mutable struct accessors are disabled in this profile.", set!.Location);
            if (isInit && !Has(ScalarFeatures.RequiredMembers)) Fail("AI3100", "An init accessor body is disabled in this profile.", set!.Location);
            string? backing = null;
            if (!declarationAccessors && (usesField || !isIndexer && (get is not null && getBody is null || set is not null && setBody is null)))
            {
                if (isIndexer) Fail("AI3106", "An indexer cannot use automatic backing storage.", name.Location);
                backing = (isStatic ? "$static_backing_" : "$instance_backing_")
                    + (!isStatic || extendedAutomatic || qualifier is not null || getPrivate ? StaticPropertyDeclaration(header) + "_" : "")
                    + (qualifier is null ? "" : qualifier + "_") + name.Text;
                fields.Add(new(backing, type, storage, false, header.IsReadonly&&!isStatic || extendedAutomatic && set is null || Has(ScalarFeatures.RequiredMembers) && getBody is null && isInit, name.Location)
                    { Hidden = true, IsInit = isInit && !Has(ScalarFeatures.RequiredMembers), WritePrivate = setPrivate, Owner = isStatic ? IndexOf(header.Id) : -1 });
            }
            string? getter = null, setter = null;
            var suffix = name.Text + (isIndexer ? "__indexer_" + properties.Count(p => p.IsIndexer) : "");
            string Add(string kind, Lexeme keyword, IReadOnlyList<ParameterDeclaration> parameters, string result, AccessorBody? body)
            {
                var accessorName = (qualifier is null ? "" : qualifier + ".") + kind + "_" + suffix;
                var modifiers = new HashSet<string>(memberModifiers, StringComparer.Ordinal) { "accessor", "accessor-member:" + kind + ":" + name.Text };
                var privateAccessor = kind == "get" ? getPrivate : setPrivate;
                if (privateAccessor && Has(ScalarFeatures.ExtendedAccessors)) modifiers.ExceptWith(["virtual","override","sealed","abstract"]);
                if (extendedAutomatic && !header.IsClass && kind == "get") modifiers.Add("readonly");
                if (readonlyAccessors.Contains(kind)) modifiers.Add("readonly");
                if (kind == "set" && isInit) modifiers.Remove("readonly");
                if (body is not null) modifiers.Add("concrete-accessor");
                else if (declarationAccessors) modifiers.Add("abstract");
                if (isStatic) modifiers.Add("static");
                if (backing is not null) modifiers.Add("backing:" + backing);
                if (isInit && kind == "set") modifiers.Add("init-accessor");
                var source = new ScalarSource(body?.Text ?? "", body?.Location ?? keyword.Location);
                if (body?.Prefix is { } prefix) accessorPrefixes![source] = prefix;
                var function = new FunctionDeclaration(kind + "_" + suffix, parameters, result, [], [], new FunctionBody([])
                    { ScalarSource = source }, [], keyword.Location);
                methods.Add(new(accessorName, keyword.Location.Offset, keyword.Location.Offset, keyword.Location.Offset,
                    qualifier is null && isPublic && !privateAccessor, modifiers, function) { InterfaceQualifier = qualifier,IsProtectedConstructor=memberModifiers.Contains("protected-constructor") });
                return header.Name + "." + accessorName;
            }
            if (get is not null && (getBody is not null || declarationAccessors)) getter = Add("get", get, indexParameters, typeToken.Text, getBody);
            if (set is not null && (setBody is not null || declarationAccessors)) setter = Add("set", set, [.. indexParameters, new("value", typeToken.Text)], "unit", setBody);
            properties.Add(new(qualifier is null ? name.Text : qualifier + "." + name.Text, type, typeToken.Text, qualifier is null && isPublic, get is not null, getter, set is not null, setter, setPrivate, backing, name.Location)
                { IsProtectedMember=memberModifiers.Contains("protected-constructor"),IsInternal=isInternal,IsRequired = isRequired, IsInit = isInit, IsStatic = isStatic, ReferenceMode = referenceMode, IsIndexer = isIndexer, IsAbstract=abstractMember,
                  IndexParameters = indexParameters, IsOverride = memberModifiers.Contains("override"), GetPrivate = getPrivate, IsAutomatic = extendedAutomatic, InterfaceQualifier = qualifier });
        }

        private ScalarExpression AccessorCall(ScalarExpression receiver, ScalarProperty property, string function, IReadOnlyList<ScalarExpression> arguments, SourceLocation at,bool deferredBoxing=false)
        {
            var owner = _structs[property.Owner]; var method = owner.Methods.First(m => m.QualifiedName == function);
            var result = method.Declaration.ReturnType == "unit" ? ScalarType.Unit : property.Type;
            if (owner.IsInterface && method.IsStatic && method.Slot is not null) return StaticInterfaceAccessorCall(receiver, property, method, arguments, result, at);
            if (_baseAccessorReceivers.Contains(receiver) && method.IsAbstract) Fail("AI3106", "An abstract accessor cannot be invoked through base.", at);
            if (owner.IsInterface && !IsClass(receiver.Type) && !StructOf(receiver.Type).IsTypeParameter)
            {
                var implementation = ResolveInterfaceImplementation(IndexOf(receiver.Type), method).Method;
                if (implementation is null || _structs[implementation.Owner].IsInterface&&!Has(ScalarFeatures.Boxing)) Fail("AI3100", "A default interface accessor on a value type requires boxing.", at);
                if(Has(ScalarFeatures.Boxing)&&NeedsBoxedAccessorBridge(receiver.Type,property)
                    &&(deferredBoxing||!_structs[implementation!.Owner].IsInterface&&!WritableAccessorReceiver(receiver)))return BoxedAccessorCall(receiver,property,method,implementation!,arguments,result,at);
                if(_structs[implementation!.Owner].IsInterface)receiver=BoxValue(receiver,owner.Id,receiver:true);
                else return InterfaceImplementationCall(implementation, receiver, arguments.ToList(), result, at);
            }
            if (method.Slot is { } slot && !_baseAccessorReceivers.Contains(receiver))
            {
                var rootOwner = _structs.First(t => t.Name == slot[..slot.LastIndexOf('.')]);
                return ReferenceResult(new("call", result, at, slot + ".virtual") { Arguments = [Upcast(receiver, rootOwner.Id), .. arguments] }, method.Declaration.ReturnType, method.Declaration.Parameters, 1);
            }
            return AccessorReferenceResult(ReferenceResult(new("call", result, at, function) { Arguments = property.IsStatic ? arguments : [AccessorReceiverArgument(method, receiver, at), .. arguments] }, method.Declaration.ReturnType, method.Declaration.Parameters, property.IsStatic ? 0 : 1), method);
        }

        private IEnumerable<ScalarProperty> IndexersOf(int type)
        {
            var seen = new List<ScalarProperty>();
            var owners = new List<int>();
            for (var k = type; k >= 0; k = _structs[k].BaseIndex) owners.Add(k);
            if (_structs[type].IsInterface || _structs[type].IsTypeParameter) owners.AddRange(InterfaceClosure(_structs, _structs[type].Interfaces));
            foreach (var owner in owners.Distinct()) foreach (var property in _structs[owner].Properties.Where(p => p.IsIndexer && p.InterfaceQualifier is null))
                if (!seen.Any(p => SameIndexSignature(p.IndexParameters, property.IndexParameters) && (p.Owner == property.Owner || NominalDerives(_structs, p.Owner, property.Owner)))) { seen.Add(property); yield return property; }
        }
        private ScalarType IndexParameterType(ScalarProperty property, ParameterDeclaration parameter, SourceLocation at)
            => MemberType(_structs[property.Owner].TypeArguments,parameter.Type,at);
        private bool ApplicableIndexArgument(ScalarExpression argument, ScalarProperty property, ParameterDeclaration parameter, SourceLocation at)
        {
            var mode = ParameterPassing(parameter.Type).Mode; var type = IndexParameterType(property,parameter,at);
            if (ReferenceArgument(argument)) return mode != ScalarParameterMode.Value && argument.Operation is "argument-in" or "argument-ref" && argument.Type == type;
            return TryCoerce(argument, type) is not null;
        }
        private ScalarExpression IndexerRead(ScalarExpression receiver)
        {
            var at = Next(); var arguments = new List<ScalarExpression>();
            if (!Take("]")) { do { var argument = CallArgument(); arguments.Add(argument); } while (Take(",")); Expect("]"); }
            var checkedIndexer = CheckedIndexer(receiver,at.Location);
            var candidates = (checkedIndexer is null ? IndexersOf(IndexOf(receiver.Type)) : [checkedIndexer]).Where(p => p.IndexParameters.Count == arguments.Count)
                .Where(p => p.IndexParameters.Select((parameter, i) => ApplicableIndexArgument(arguments[i], p, parameter, at.Location)).All(v => v)).ToList();
            bool Better(ScalarProperty a, ScalarProperty b)
            {
                var better = false;
                for (var i = 0; i < arguments.Count; i++)
                {
                    var left = IndexParameterType(a,a.IndexParameters[i],at.Location); var right = IndexParameterType(b,b.IndexParameters[i],at.Location);
                    if (left == right)
                    {
                        var lm = ParameterPassing(a.IndexParameters[i].Type).Mode; var rm = ParameterPassing(b.IndexParameters[i].Type).Mode;
                        if (lm != rm && !ReferenceArgument(arguments[i]))
                        { if (lm == ScalarParameterMode.Value) better = true; else if (rm == ScalarParameterMode.Value) return false; }
                        continue;
                    }
                    if (arguments[i].Type == left) { better = true; continue; }
                    if (arguments[i].Type == right) return false;
                    var ltr = TryCoerce(new("local", left, at.Location, "$overload"), right) is not null;
                    var rtl = TryCoerce(new("local", right, at.Location, "$overload"), left) is not null;
                    if (!ltr || rtl) return false;
                    better = true;
                }
                return better;
            }
            candidates = candidates.Where(candidate => !candidates.Any(other => !ReferenceEquals(candidate, other) && Better(other, candidate))).ToList();
            if (candidates.Count != 1) Fail(candidates.Count == 0 ? "AI3116" : "AI3106", candidates.Count == 0 ? "No applicable indexer." : "Ambiguous indexer access.", at.Location);
            var property = candidates[0];
            if (StructOf(receiver.Type).IsTypeParameter && _structs[property.Owner].IsInterface) constrainedIndexers![at.Location.Offset] = property;
            for (var i = 0; i < arguments.Count; i++) arguments[i] = BindArgument(arguments[i], IndexParameterType(property,property.IndexParameters[i],arguments[i].Location), ParameterPassing(property.IndexParameters[i].Type).Mode);
            CompleteArguments(arguments);
            return PropertyRead(receiver, property, at, arguments);
        }
        private ScalarExpression AccessorMutation(ScalarExpression read, ScalarExpression receiver, ScalarProperty property,
            IReadOnlyList<ScalarExpression> indices, string setter, string op, ScalarExpression right, bool postfix, SourceLocation at)
        {
            right = AccessorOperand(right);
            var setCall = AccessorCall(receiver, property, setter, indices, at,deferredBoxing:op!="=");
            if (op == "=") return new("accessor-mutation", property.Type, at, "store", Left: DelayedBoxedAccessorStore(setCall,receiver,property,setter,at), Right: Coerce(right, property.Type));
            Read(read);
            var old = new ScalarExpression("reference-read", property.Type, read.Location);
            var getter = CaptureAccessorReceiver(Has(ScalarFeatures.Boxing)&&IsStruct(receiver.Type)&&!IsClass(receiver.Type)&&property.Getter is {} get&&_structs[property.Owner].IsInterface&&NeedsBoxedAccessorBridge(receiver.Type,property)
                ? AccessorCall(receiver,property,get,indices,read.Location,deferredBoxing:true):read, setCall);
            return new("accessor-mutation", property.Type, at, postfix ? "post" : "pre", Left: getter, Right: CompoundValue(old, op[..^1], right, at)) { AccessorSetter = setCall.Value };
        }
        // Nested stores are values in an accessor RHS. Reuse the checked reference-store/update
        // path so a side effect cannot cause either the target or its old value to be evaluated twice.
        private static ScalarExpression AccessorOperand(ScalarExpression expression)
        {
            var e = expression with { Left = expression.Left is null ? null : AccessorOperand(expression.Left),
                Right = expression.Right is null ? null : AccessorOperand(expression.Right), Arguments = expression.Arguments?.Select(AccessorOperand).ToArray() };
            if (expression is not { Operation: "=", Left: not null, Right: not null }) return e;
            if (expression.Value != "compound") return e with { Operation = "reference-store", Value = null };
            ScalarExpression Replace(ScalarExpression node) => ReferenceEquals(node, expression.Left) ? new("reference-read", node.Type, node.Location) : node with
                { Left = node.Left is null ? null : Replace(node.Left), Right = node.Right is null ? null : Replace(node.Right), Arguments = node.Arguments?.Select(Replace).ToArray() };
            return e with { Operation = "reference-update", Value = "pre", Right = AccessorOperand(Replace(expression.Right)) };
        }
    }
}
