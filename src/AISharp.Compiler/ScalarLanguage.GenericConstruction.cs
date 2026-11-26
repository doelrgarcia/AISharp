namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private ExtendedBinder(FunctionDeclaration function, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> all,
            ScalarStruct owner, ScalarStructMethod constructor, string initializerName)
            : this(function, features, all, owner, constructor, init: true)
        { _qualifiedName = initializerName; }

        private ScalarExpression NewReferenceTypeParameter(ScalarStruct info, Lexeme token, SourceLocation at)
        {
            if (Take("(")) Expect(")");
            else if (Current.Text != "{") Fail("AI3106", "Expected '(' or '{' after the type parameter in 'new'.");
            var name = info.Name + ".<generic-new>";
            if (requestedInstances!.Add(name))
            {
                var index = IndexOf(info.Id);
                var constructor = info.Methods.FirstOrDefault(m => m.IsConstructor&&(!RecordConstructorsEnabled(info)||m.Declaration.Parameters.Count==0&&m.IsPublic));
                if (constructor is null)
                {
                    var declaration = new FunctionDeclaration("new", [], info.Name, [], [],
                        new FunctionBody([]) { ScalarSource = new("", at) }, [], at);
                    constructor = new("new", info.Name + ".new", true, true, declaration) { Owner = index };
                }
                var initializerName = info.Name + ".<generic-init>";
                var runName = info.Name + ".<generic-new-run>";
                // The initializer guard runs inside the exception wrapper. Allocation stays outside it.
                var current = _structs[index];
                var metadata = new ScalarStructMethod("<generic-init>", initializerName, true, false, constructor.Declaration)
                    { Owner = index, IsStatic = true, IsLifted = true };
                _structs[index] = current with { Methods = [.. current.Methods, metadata] };
                const string selfName = "__generic_new_self";
                if(info.IsRecord&&NeedsRecordConstruction(info))
                {
                    var receiver=new ScalarExpression("local",info.Id,at,selfName);
                    var initialize=RecordConstructorsEnabled(info)?RecordConstructorCall(info,constructor,receiver,[],at):new ScalarExpression("call",info.Id,at,info.Name+"."+RecordInitializerName(info)){Arguments=[receiver]};
                    Lift(new ScalarFunction(initializerName,[new ScalarParameter(selfName,info.Id)],ScalarType.Unit,[new ScalarStatement("expression",at,initialize)],[TrueContract(at)],at));
                }
                else Lift(new ExtendedBinder(constructor.Declaration, _features, _functions, info, constructor, initializerName).Bind());
                var self = new ScalarExpression("local", info.Id, at, selfName);
                var invoke = new ScalarExpression("call", ScalarType.Unit, at, initializerName) { Arguments = [self] };
                var body = new ScalarStatement("try", at,
                    Body: [new ScalarStatement("expression", at, invoke)],
                    Alternative: [new ScalarStatement("catch", at, Name: "*", Body:
                        [new ScalarStatement("throw", at, Name: "System.Reflection.TargetInvocationException")])]);
                Lift(new ScalarFunction(runName, [new ScalarParameter(selfName, info.Id)], ScalarType.Unit,
                    [body], [TrueContract(at)], at));
                var run = new ScalarExpression("call", ScalarType.Unit, at, runName) { Arguments = [self] };
                Lift(new ScalarFunction(name, [], info.Id,
                    [new ScalarStatement("local", at, new ScalarExpression("new-uninitialized", info.Id, at), selfName, info.Id),
                     new ScalarStatement("expression", at, run), new ScalarStatement("return", at, self)], [TrueContract(at)], at));
            }
            var created = new ScalarExpression("call", info.Id, at, name) { Arguments = [] };
            return Has(ScalarFeatures.RequiredMembers) ? RequiredObjectInitializer(info, created, token.Location, enforceRequired: false) : created;
        }
    }
}
