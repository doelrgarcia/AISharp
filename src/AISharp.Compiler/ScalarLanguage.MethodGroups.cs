namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private const string DelegateTypeDeclarationPattern = @"(?m)^[ \t]*(?:(?:public|abstract|sealed|static|readonly)[ \t]+)*(?:record(?:[ \t]+(?:class|struct))?|class|struct|interface|enum)[ \t]+(" + IdentifierSpelling.Pattern + @")(?:[ \t]*<([^>\r\n]+)>)?";
    private static bool KnownDelegateSignature(string type, IReadOnlyDictionary<string, int> arities, IReadOnlySet<string> delegates, int depth, IReadOnlySet<string>? parameters = null)
    {
        if (depth > 256) return false;
        var importedSyntax=ImportSignatureSyntax(type,parameters);if(importedSyntax!=type)return KnownDelegateSignature(importedSyntax,arities,delegates,depth+1,parameters);
        var canonical=IdentifierName(type);
        if(CanBindTypeParameter(type) && parameters?.Contains(canonical)==true)return true;
        if(IsEscapedIdentifier(type))return delegates.Contains(canonical)&&arities.GetValueOrDefault(canonical,-1)==0 || arities.GetValueOrDefault(canonical,-1)==0;
        if (type.EndsWith("[]",StringComparison.Ordinal) && ((activeFeatures & ScalarFeatures.TypedArrays) != 0 || type == "string[]" && (activeFeatures & ScalarFeatures.ProcessArguments) != 0))
            return KnownDelegateSignature(type[..^2],arities,delegates,depth+1,parameters);
        if (DelegateSignatureTypes.Contains(type) || ScalarConstants.FromSpelling(type) is not null || parameters?.Contains(type) == true || delegates.Contains(type) && arities.GetValueOrDefault(type, -1) == 0 || type == "IDisposable") return true;
        var open = type.IndexOf('<');
        if (open < 0) return arities.GetValueOrDefault(type, -1) == 0 || (activeFeatures & ScalarFeatures.Async) != 0 && type == "Task";
        if (!type.EndsWith('>') || type.Any(c => !char.IsLetterOrDigit(c) && !char.IsSurrogate(c) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.LetterNumber or System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation or System.Globalization.UnicodeCategory.Format) && c is not ('_' or '@' or '\\' or '<' or '>' or ',' or '[' or ']'))) return false;
        var nesting = 0;
        for (var i = open; i < type.Length; i++)
        {
            if (type[i] == '<') nesting++;
            else if (type[i] == '>') nesting--;
            if (nesting < 0 || nesting == 0 && i != type.Length - 1) return false;
        }
        if (nesting != 0) return false;
        var name = IdentifierName(type[..open]); if (parameters?.Contains(name) == true) return false; var arguments = SplitTypeList(type[(open + 1)..^1]).ToArray();
        var expected = arities.GetValueOrDefault(name, -1);
        if ((activeFeatures & ScalarFeatures.Async) != 0 && name is "Task" or "TaskCompletionSource") expected = 1;
        return expected > 0 && arguments.Length == expected && arguments.All(t => KnownDelegateSignature(t, arities, delegates, depth + 1, parameters));
    }

    private static void ValidateDelegateSignatures(ScalarFeatures features, string source)
    {
        if (activeDelegates is null) return;
        foreach (var info in activeDelegates.Values)
        {
            if ((features & (ScalarFeatures.Classes | ScalarFeatures.Functions)) != (ScalarFeatures.Classes | ScalarFeatures.Functions))
                throw DeclarationError("AI3100", "Delegates require class and function support.", info.Location);
            var owner = activeStructs!.FirstOrDefault(s => s.Name == info.Name)
                ?? throw DeclarationError("AI3100", "Delegates require declaration support in this profile.", info.Location);
            foreach (var (type, at) in info.SignatureLocations)
            {
                var binder = new ExtendedBinder(features, "", at);
                if (owner.TypeArguments is not null) binder.UseTypeArguments(new Dictionary<string, ScalarType>(owner.TypeArguments), true);
                binder.HierarchyType(type, at);
            }
            for (var i = 0; i < info.Parameters.Count; i++) CheckVarianceSafety(owner, info.Parameters[i].Type, -1, info.SignatureLocations[i].Location);
            CheckVarianceSafety(owner, info.Return, 1, info.ReturnLocation);
        }
    }

    private sealed record MethodGroupDispatch(ScalarStructMethod Root, ScalarType Receiver, ScalarType Delegate, IReadOnlyList<ScalarType>? Arguments, string Name, SourceLocation Location)
    { public int BuiltTypeCount { get; set; } = -1; }
    [ThreadStatic] private static Dictionary<string, MethodGroupDispatch>? methodGroupDispatches;

    private static bool EmitMethodGroupDispatches(List<ScalarFunction> members, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> functions)
    {
        foreach (var request in methodGroupDispatches!.Values.ToArray())
        {
            if (request.BuiltTypeCount == activeStructs!.Count) continue;
            var root = request.Root; var owner = activeStructs[root.Owner];
            var arguments = owner.TypeArguments is null ? new Dictionary<string, ScalarType>() : new Dictionary<string, ScalarType>(owner.TypeArguments);
            if (request.Arguments is { } closed)
            {
                var generic = ExtendedBinder.GenericOf(root.Declaration)!;
                for (var i = 0; i < generic.Parameters.Count; i++) arguments[generic.Parameters[i].Name] = closed[i];
            }
            var binder = new ExtendedBinder(root.Declaration, features, functions, owner, root, arguments, request.Name);
            var function = binder.BuildMethodGroupDispatch(request);
            var old = members.FindIndex(f => f.Name == request.Name);
            if (old < 0) members.Add(function); else members[old] = function;
            request.BuiltTypeCount = activeStructs.Count;
        }
        return pendingInstances!.Count > 0 || pendingMethodInstances!.Count > 0;
    }

    private sealed partial class ExtendedBinder
    {
        // A type argument list is part of a method group unless followed by an invocation.
        private bool MethodGroupAhead()
        {
            if (!Delegating || Current.Text == "(") return false;
            if (Current.Text != "<") return true;
            var depth = 0;
            for (var position = _position; position < _tokens.Count; position++)
            {
                var token = TokenAt(position).Text;
                if (token == "<") depth++;
                else if (token.Length > 0 && token.All(c => c == '>')) depth -= token.Length;
                else if (token is "" or "\n" or "{" or "}") return false;
                if (depth <= 0) return TokenAt(position + 1).Text != "(";
            }
            return false;
        }

        private ScalarExpression MethodGroupExpression(Lexeme member, ScalarExpression? receiver = null)
        {
            List<ScalarExpression>? arguments = null;
            if (Has(ScalarFeatures.Generics) && Take("<"))
            {
                arguments = [];
                do { var token = TypeToken(); arguments.Add(new("type-argument", Type(token.Text, token.Location), token.Location)); } while (Take(","));
                Expect(">");
            }
            return new("methodgroup", (ScalarType)MethodGroupTypeId, member.Location, member.Text, Left: receiver) { Arguments = arguments };
        }

        private bool ExtendedMethodGroup(ScalarExpression site)
        {
            if (site.Arguments is not null) return true;
            if (site.Left is null) return _functions.FirstOrDefault(f => f.Name == site.Value) is { } function && GenericOf(function) is not null;
            var owner = StructOf(site.Left.Type);
            if (site.Left.Value is { } qualifier && site.Left.Operation == "static-type" && _typeArgs?.ContainsKey(qualifier) == true) return true;
            if (owner.IsInterface || owner.IsTypeParameter || _typeArgs is not null && constrainedInterfaceCalls!.ContainsKey(site.Location.Offset)) return true;
            var method = FindMethod(IndexOf(owner.Id), site.Value!);
            return method is not null && (GenericOf(method.Declaration) is not null || _structs[method.Owner].IsInterface);
        }

        private ScalarStructMethod BoundInstanceMethodGroup(ScalarExpression site,ScalarStructMethod? preferred=null)
        {
            var info = StructOf(site.Left!.Type); ScalarStructMethod? method;
            if(preferred is not null)method=preferred;
            else if (_typeArgs is not null && (!_checkOnly || !info.IsTypeParameter) && constrainedInterfaceCalls!.TryGetValue(site.Location.Offset, out var checkedRoot))
            {
                method = ReboundInterfaceRoot(checkedRoot);
                if (!Derives(_structs, IndexOf(info.Id), method.Owner)) Fail("AI3107", "The closed receiver does not satisfy its declared interface proof.", site.Location);
            }
            else method = FindMethod(IndexOf(info.Id), site.Value!);
            if (info.IsInterface && InterfaceMethods([IndexOf(info.Id)], site.Value!).Count > 1)
                Fail("AI3106", "The method group is ambiguous between inherited interface members.", site.Location);
            if (method is null && info.IsTypeParameter)
            {
                var candidates = InterfaceMethods(info.Interfaces, site.Value!);
                if (candidates.Count > 1) Fail("AI3106", "The method group is ambiguous between interface constraints.", site.Location);
                if (candidates.Count == 1) method = candidates[0];
            }
            if (method is null) { Fail("AI3103", $"'{info.Name}' has no method '{site.Value}'.", site.Location); return null!; }
            var declaring = _structs[method.Owner];
            if (_checkOnly && info.IsTypeParameter && declaring.IsInterface) constrainedInterfaceCalls![site.Location.Offset] = method;
            if (!info.IsClass&&!Has(ScalarFeatures.Boxing)) Fail("AI3100", $"Converting a method of the struct '{info.Name}' to a delegate requires boxing, which is not supported yet.", site.Location);
            if (method.IsStatic) Fail("AI3106", "A static method must be accessed through its type.", site.Location);
            CheckDisplayMethodAccess(site.Left!,declaring,method,new(site.Value!,site.Location),false);
            return method;
        }

        private Dictionary<string, ScalarType> MethodGroupArguments(FunctionDeclaration declaration, ScalarStruct? owner, ScalarExpression site, IReadOnlyList<ScalarType> types, out IReadOnlyList<ScalarType>? arguments)
        {
            var generic = GenericOf(declaration);
            if (generic is null)
            {
                if (site.Arguments is not null) Fail("AI3116", "A non-generic method group cannot have type arguments.", site.Location);
                arguments = null; return owner?.TypeArguments is { } known ? new(known) : [];
            }
            var values = types.Select((t, i) => new ScalarExpression("local", t, site.Location, "__delegate_argument_" + i)).ToList();
            var explicitArguments = site.Arguments?.Select(a => (a.Type, a.Location)).ToList();
            var map = BindGenericArguments(new(site.Value!, site.Location), declaration, generic, explicitArguments, values, owner);
            arguments = generic.Parameters.Select(p => map[p.Name]).ToArray();
            return map;
        }

        private ScalarExpression ConvertBoundMethodGroup(ScalarExpression site, ScalarType target)
        {
            var info = DelegateOf(target); var at = site.Location;
            var types = info.Parameters.Select(p => Type(p.Type, at)).ToArray(); var result = Type(info.Return, at, true);
            ScalarStructMethod? method = null; ScalarStruct? owner = null; FunctionDeclaration declaration;
            if (site.Left is null) declaration = _functions.First(f => f.Name == site.Value);
            else
            {
                if (site.Left.Operation == "static-type")
                {
                    var constrained = site.Left.Value is { } qualifier && _typeArgs?.ContainsKey(qualifier) == true;
                    method = constrained ? StaticInterfaceMember(site.Left, new(site.Value!, at)).Root : (site.Value=="Deconstruct"&&Has(ScalarFeatures.RecordMembers)||site.Value=="Equals"&&Has(ScalarFeatures.RecordInheritance)&&RecordEqualityReceiver(StructOf(site.Left.Type))||Has(ScalarFeatures.RecordDisplay)&&site.Value is "ToString" or "PrintMembers"&&RecordEqualityReceiver(StructOf(site.Left.Type))||Has(ScalarFeatures.RecordHashing)&&site.Value=="GetHashCode"&&RecordEqualityReceiver(StructOf(site.Left.Type)))&&!StructOf(site.Left.Type).IsInterface?SelectDeconstructGroup(site,info.Parameters,types):FindMethod(IndexOf(site.Left.Type), site.Value!)!;
                    if (method is null) { Fail("AI3103", $"No method '{site.Value}'.", at); return null!; }
                    owner = _structs[method.Owner];
                    if (!method.IsStatic) Fail("AI3106", "An instance method requires an object receiver.", at);
                    if (!constrained && owner.IsInterface && method.Slot is not null) Fail("AI3106", "A static abstract or virtual interface member can be accessed only on a constrained type parameter.", at);
                    if (!constrained) CheckAccess(owner, method.IsPublic, site.Value!, at);
                }
                else method = BoundInstanceMethodGroup(site,(site.Value=="Deconstruct"&&Has(ScalarFeatures.RecordMembers)||site.Value=="Equals"&&Has(ScalarFeatures.RecordInheritance)&&RecordEqualityReceiver(StructOf(site.Left.Type))||Has(ScalarFeatures.RecordDisplay)&&site.Value is "ToString" or "PrintMembers"&&RecordEqualityReceiver(StructOf(site.Left.Type))||Has(ScalarFeatures.RecordHashing)&&site.Value=="GetHashCode"&&RecordEqualityReceiver(StructOf(site.Left.Type)))&&!StructOf(site.Left.Type).IsInterface&&(!StructOf(site.Left.Type).IsTypeParameter||site.Value=="Equals")?SelectDeconstructGroup(site,info.Parameters,types):null);
                owner = _structs[method.Owner]; declaration = method.Declaration;
            }
            if (declaration.Parameters.Count != info.Parameters.Count || !declaration.Parameters.Zip(info.Parameters).All(p => CompatibleReferenceMethod(ParameterPassing(p.First.Type).Mode, ParameterPassing(p.Second.Type).Mode))) Fail("AI3107", "Method and delegate parameter passing modes do not match.", at);
            var map = MethodGroupArguments(declaration, owner, site, types, out var arguments);
            var resultMode = ParameterPassing(info.Return).Mode;
            if (resultMode != ScalarParameterMode.Value && declaration.Parameters.Zip(info.Parameters).Any(p=>ScopedReference(p.Second.Type) && !ScopedReference(p.First.Type))) Fail("AI3107", "Method and delegate scoped contracts do not match.", at);
            if (ParameterPassing(declaration.ReturnType).Mode != resultMode || resultMode != ScalarParameterMode.Value && MemberType(map, declaration.ReturnType, at, true) != result) Fail("AI3107", "Method and delegate reference returns must have identical modes and element types.", at);
            bool Converts(ScalarType from, ScalarType to) => ReferenceConvertible(_structs, from, to, []);
            if (declaration.Parameters.Count != types.Length || !types.Select((t, i) => ParameterPassing(declaration.Parameters[i].Type).Mode == ScalarParameterMode.Value ? Converts(t, MemberType(map, declaration.Parameters[i].Type, at)) : t == MemberType(map, declaration.Parameters[i].Type, at)).All(ok => ok)
                || !Converts(MemberType(map, declaration.ReturnType, at, true), result))
                Fail("AI3107", $"No overload for '{site.Value}' matches delegate '{info.Name}'.", at);
            if (_checkOnly || !DelegateReady(info)) return new("new", target, at);
            if(Has(ScalarFeatures.Boxing)&&method is {IsStatic:false}&&site.Left is not null&&!IsClass(site.Left.Type))
            {
                var implementation=method;
                if(_structs[method.Owner].IsInterface)implementation=ResolveInterfaceImplementation(IndexOf(site.Left.Type),method).Method??method;
                var box=ValueBox(site.Left.Type,at);var boxed=new ScalarExpression("box-value",box.Id,at,((int)site.Left.Type).ToString(System.Globalization.CultureInfo.InvariantCulture),Left:site.Left);
                var adapter=_structs[implementation.Owner].IsInterface?implementation:box.Methods.Single(m=>m.BoxedMethodTarget==implementation.QualifiedName);
                return LiftBoundMethodDelegate(boxed,adapter,null,target,arguments,at);
            }
            if (site.Left?.Operation == "static-type" && site.Left.Value is { } written && _typeArgs?.ContainsKey(written) == true)
                method = StaticInterfaceMember(site.Left, new(site.Value!, at)).Implementation;
            if (method is not null && !method.IsStatic && _structs[method.Owner].IsInterface && method.Slot is not null)
            {
                var key = ((int)site.Left!.Type) + ":" + method.QualifiedName + ":" + ((int)target) + ":" + string.Join(",", arguments ?? []);
                if (!methodGroupDispatches!.TryGetValue(key, out var request))
                {
                    if (methodGroupDispatches.Count >= 256) Fail("AI3100", "More than 256 method-group dispatch bindings are not supported.", at);
                    request = new(method, site.Left.Type, target, arguments, "__mg_factory<" + methodGroupDispatches.Count + ">", at); methodGroupDispatches[key] = request;
                }
                return new("call", target, at, request.Name) { Arguments = [site.Left] };
            }
            return LiftBoundMethodDelegate(site.Left, method, method is null ? declaration : null, target, arguments, at);
        }

        private ScalarExpression LiftBoundMethodDelegate(ScalarExpression? receiver, ScalarStructMethod? method, FunctionDeclaration? function, ScalarType target, IReadOnlyList<ScalarType>? typeArguments, SourceLocation at)
        {
            var info = DelegateOf(target); var types = info.Parameters.Select(p => Type(p.Type, at)).ToArray(); var result = Type(info.Return, at, true);
            var methodOwner = method is null ? null : _structs[method.Owner];
            var holder = method is { IsStatic: false } ? methodOwner : null;
            // Reuse the existing class-method identity: virtual overrides share their root slot and target.
            if (holder is { IsInterface: false } && method!.Slot is { } slot) holder = _structs.First(t => t.Name == slot[..slot.LastIndexOf('.')]);
            var identity = method is null ? "fn:" + function!.Name : method.IsStatic ? "static:" + method.QualifiedName : "m:" + (methodOwner!.IsInterface ? method.QualifiedName : method.Slot ?? method.QualifiedName);
            var key = identity + (typeArguments is null ? "" : "<" + string.Join(", ", typeArguments.Select(Show)) + ">") + ":" + info.Name;
            var fnType = _structs[FunctionClassOf(info)].Id;
            if (!syntheticClasses!.TryGetValue(key, out var k))
            {
                var fields = holder is null ? new List<ScalarStructField>() : [new ScalarStructField("target", holder.Id, holder.Id, true, false, at)];
                var (index, id, className) = NewLiftedClass("__mg", info, fields, at); k = index; syntheticClasses[key] = k;
                var self = new ScalarParameter(Fresh(), id); var selfRead = new ScalarExpression("local", id, at, self.Name);
                var parameters = types.Select((t, i) => new ScalarParameter(Fresh(), t) { Mode = ParameterPassing(info.Parameters[i].Type).Mode, Scoped = ScopedReference(info.Parameters[i].Type) }).ToList();
                var values = parameters.Select(p => ForwardParameter(p, at)).ToList();
                var targetRead = holder is null ? null : new ScalarExpression("field", holder.Id, at, "0", Left: selfRead);
                ScalarExpression call;
                if (typeArguments is not null)
                {
                    var actual = method is null ? function! : method.Declaration;
                    call = GenericCall(new(method?.Name ?? function!.Name, at), actual, GenericOf(actual)!, typeArguments.Select(t => (t, at)).ToList(), values, methodOwner, method, targetRead,
                        methodOwner?.IsInterface == true || method?.InterfaceQualifier is not null || method?.Slot is null || method.IsStatic);
                }
                else if (method is not null && (method.IsStatic || methodOwner!.IsInterface || method.InterfaceQualifier is not null))
                    call = InterfaceImplementationCall(method, targetRead, values, result, at);
                else call = MethodCall(targetRead!, new(method!.Name, at), false, values);
                Lift(new ScalarFunction(className + ".Run", [self, .. parameters], result,
                    result == ScalarType.Unit ? [new("expression", at, call)] : [Returning(Coerce(call, result), ParameterPassing(info.Return).Mode, at)], [TrueContract(at)], at) { ReturnMode = ParameterPassing(info.Return).Mode });
                Lift(SameFunction(className + ".Same", id, DelegateSameType(info), _structs[k].Fields, at));
                if (holder is not null)
                {
                    var p = new ScalarParameter(Fresh(), holder.Id); var read = new ScalarExpression("local", holder.Id, at, p.Name);
                    var fault = method!.Slot is not null || methodOwner!.IsInterface ? "System.NullReferenceException" : "System.ArgumentException";
                    Lift(new ScalarFunction(className + "_check", [p], holder.Id,
                        [new("if", at, new ScalarExpression("==", ScalarType.Bool, at, Left: read, Right: new("null", holder.Id, at)) { Wraps = _wraps }, Body: [new("throw", at, Name: fault)], Alternative: []),new("return", at, read)], [TrueContract(at)], at));
                }
            }
            var lifted = _structs[k];
            ScalarExpression callable = holder is null ? new("new", lifted.Id, at) : new("init", lifted.Id, at, "0", Left: new("new", lifted.Id, at))
                { Arguments = [new ScalarExpression("call", holder.Id, at, lifted.Name + "_check") { Arguments = [Upcast(receiver!, holder.Id)] }] };
            return WrapDelegate(target, fnType, callable, at);
        }

        public ScalarFunction BuildMethodGroupDispatch(MethodGroupDispatch request)
        {
            var at = request.Location; var parameter = new ScalarParameter(Fresh(), request.Receiver); var receiver = new ScalarExpression("local", request.Receiver, at, parameter.Name);
            var concrete = ConcreteOf(_structs, IndexOf(request.Receiver)).ToArray();
            ScalarStatement Bind(int type)
            {
                var resolution = ResolveInterfaceImplementation(type, request.Root);
                if (resolution.Ambiguous || resolution.Method is null) { Fail("AI3107", "The receiver has no unique method-group implementation.", at); return null!; }
                var implementation = resolution.Method!; var owner = _structs[implementation.Owner];
                var target = owner.Id == request.Receiver ? receiver : new ScalarExpression("downcast", owner.Id, at, Left: receiver);
                return new("return", at, LiftBoundMethodDelegate(target, implementation, null, request.Delegate, request.Arguments, at));
            }
            List<ScalarStatement> body = concrete.Length == 0 ? [new("while", at, Literal(true, at), Body: [])] : [Bind(concrete[^1])];
            for (var i = concrete.Length - 2; i >= 0; i--)
                body = [new("if", at, new ScalarExpression("isexact", ScalarType.Bool, at, ((int)_structs[concrete[i]].Id).ToString(System.Globalization.CultureInfo.InvariantCulture), Left: receiver), Body: [Bind(concrete[i])], Alternative: body)];
            body.Insert(0, new("if", at, new ScalarExpression("==", ScalarType.Bool, at, Left: receiver, Right: new("null", request.Receiver, at)), Body: [new("throw", at, Name: "System.NullReferenceException")], Alternative: []));
            var function = new ScalarFunction(request.Name, [parameter], request.Delegate, body, [TrueContract(at)], at);
            return Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers) ? Erase(function) : function;
        }
    }
}
