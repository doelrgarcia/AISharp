using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static ScalarStructMethod? StructReceiverAccessor(string function)
        => layouts?.Where(t => !t.IsClass).SelectMany(t => t.Methods).FirstOrDefault(m => m.QualifiedName == function && m.ReceiverMode != ScalarParameterMode.Value);

    private sealed partial class Verifier
    {
        private Call VerifyCall(ScalarExpression expression)
        {
            var at = expression.Location;
            if (expression is not { Operation: "call", Value: { } callee, Arguments: { } arguments, Left: null, Right: null }
                || !signatures.TryGetValue(callee, out var signature)) throw new Rejected(at, "Call target is not a verified function.");
            if (arguments.Count != signature.Parameters.Count) throw new Rejected(at, "Call argument count does not match the callee.");
            if(layouts!.FirstOrDefault(t=>ScalarLanguage.IsRecordFactory(t,callee)) is { } factory
                &&(signature.ReturnMode!=ScalarParameterMode.Value||signature.Return!=factory.Id||!signature.Parameters.SequenceEqual(factory.RecordParameterStorageTypes)||!signature.Modes.SequenceEqual(ScalarLanguage.RecordFactoryParameterModes(factory))))
                throw new Rejected(at,"A record factory must preserve its constructor parameter and result ABI.");
            foreach(var owner in layouts!.Where(t=>t.IsRecord))
            foreach(var constructor in owner.Methods.Where(m=>m.IsConstructor&&m.RecordConstructorKind is "source" or "copy"))
            {
                var initializer=ScalarLanguage.RecordConstructorInitName(constructor)==callee;
                if(!initializer&&constructor.QualifiedName!=callee)continue;
                var expected=initializer?new[]{new ScalarParameter("this",owner.Id)}.Concat(constructor.RecordConstructorParameters).ToArray():constructor.RecordConstructorParameters;
                if(signature.Return!=owner.Id||signature.ReturnMode!=ScalarParameterMode.Value||!signature.Parameters.SequenceEqual(expected.Select(p=>p.Type))||!signature.Modes.SequenceEqual(expected.Select(p=>p.Mode))||!signature.Scoped.SequenceEqual(expected.Select(p=>p.Scoped)))
                    throw new Rejected(at,"A record constructor must preserve its complete parameter and result ABI.");
            }
            var accessor = StructReceiverAccessor(callee);
            if (accessor is not null && (signature.Modes.Count == 0 || signature.Modes[0] != accessor.ReceiverMode || !signature.Scoped[0]))
                throw new Rejected(at, "A struct accessor must preserve its scoped receiver ABI.");
            var values = new List<Value>();
            for (var i = 0; i < arguments.Count; i++)
            {
                var mode = signature.Modes[i];
                values.Add(mode == ScalarParameterMode.Value ? Require(VerifyValue(arguments[i],at),Map(signature.Parameters[i],at),arguments[i].Location)
                    : VerifyReferenceArgument(arguments[i],Map(signature.Parameters[i],at),mode,i == 0 && accessor is not null));
            }
            foreach (var argument in arguments) if (argument is { Operation: "argument-out", Left: not null }) AssignReference(argument.Left);
            if (expression.Type != signature.Return || signature.Return != ScalarType.Unit && Map(signature.Return,at) != signature.Return)
                throw new Rejected(at, "Call result type does not match the callee.");
            Callees.Add(signature.Index);
            var instance = !IsStaticFunction(callee) && callee.Contains('.',System.StringComparison.Ordinal) && !callee.EndsWith(".new",System.StringComparison.Ordinal) && values.Count > 0 && IsClassType(values[0].Type);
            return new(signature.Index,signature.Return,values,at,instance) { ReturnMode = signature.ReturnMode };
        }
    }
}
