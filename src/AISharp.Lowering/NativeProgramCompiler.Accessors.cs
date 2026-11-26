using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record AccessorMutation(Call Target, int Setter, Value Value, int Capture, bool Postfix, SourceLocation At) : Value(At)
    { public override ScalarType Type => Value.Type; }
    private sealed record AccessorBoxReceiver(ScalarType Target,ScalarType Box,ReferenceArgument Receiver,SourceLocation At):Value(At)
    { public override ScalarType Type=>Target; }
    private sealed partial class Verifier
    {
        private Value VerifyAccessorBoxReceiver(ScalarExpression expression,ScalarType target)
        {
            if(expression is not{Operation:"accessor-box",Left:not null,Right:null,Arguments:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null,RuntimeTypeShape:null,RuntimeTypeProof:null}
                ||expression.Type!=target||!IsStructType(target)||!Layout(target).IsInterface
                ||!int.TryParse(expression.Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var id)
                ||expression.Value!=id.ToString(System.Globalization.CultureInfo.InvariantCulture)||!IsStructType((ScalarType)id)||Layout((ScalarType)id).BoxedType is not {} payload)
                throw new Rejected(expression.Location,"An accessor box needs an exact interface target and verified value-box layout.");
            var capture=expression.Left;
            if(capture.Operation is not("argument-in" or "argument-temporary")||capture.Operation=="argument-in"&&capture.Value is not(null or "forwarded")||capture.Operation=="argument-temporary"&&capture.Value is not null||capture.Wraps||capture.AccessorSetter is not null||capture.ReferenceMode!=ScalarParameterMode.Value||capture.ReferenceExcludedArguments is not null||capture.RuntimeTypeShape is not null||capture.RuntimeTypeProof is not null)
                throw new Rejected(expression.Location,"An accessor box needs a readonly receiver location or temporary.");
            var argument=(ReferenceArgument)VerifyReferenceArgument(capture,BoxStorage(payload),ScalarParameterMode.In);
            var proof=new ScalarExpression("upcast",target,expression.Location,Left:new("box-value",(ScalarType)id,expression.Location,((int)payload).ToString(System.Globalization.CultureInfo.InvariantCulture),Left:capture.Left));
            _=Require(VerifyValue(proof,expression.Location),target,expression.Location);
            return new AccessorBoxReceiver(target,(ScalarType)id,argument,expression.Location);
        }
        private Value VerifyAccessorMutation(ScalarExpression e)
        {
            if (e.Left is not { Operation: "call", Value: { } function, Arguments: { } arguments, Left: null, Right: null } call || e.Right is null || e.Arguments is not null || e.Value is not ("pre" or "post" or "store"))
                throw new Rejected(e.Location, "Malformed accessor mutation.");
            if (e.Value == "store")
            {
                if (e.AccessorSetter is not null || !signatures.TryGetValue(function, out var setter) || setter.Return != ScalarType.Unit || setter.ReturnMode != ScalarParameterMode.Value || setter.Parameters.Count != arguments.Count + 1 || setter.Modes[^1] != ScalarParameterMode.Value || setter.Parameters[^1] != e.Type)
                    throw new Rejected(e.Location, "Accessor store does not match a verified setter.");
                var values = arguments.Select((a,i) => setter.Modes[i] == ScalarParameterMode.Value ? i==0&&a.Operation=="accessor-box"?VerifyAccessorBoxReceiver(a,setter.Parameters[i]):Require(VerifyValue(a,a.Location),setter.Parameters[i],a.Location) : VerifyReferenceArgument(a,setter.Parameters[i],setter.Modes[i],i == 0 && StructReceiverAccessor(function) is not null)).ToList();
                var value = Require(VerifyValue(e.Right,e.Location),e.Type,e.Location); Callees.Add(setter.Index);
                var instance = !IsStaticFunction(function) && values.Count > 0 && IsClassType(values[0].Type);
                return new AccessorMutation(new(setter.Index,ScalarType.Unit,values,e.Location,instance),setter.Index,value,-1,false,e.Location);
            }
            if (e.AccessorSetter is null || !signatures.TryGetValue(e.AccessorSetter,out var target)
                || target.Return != ScalarType.Unit || target.ReturnMode != ScalarParameterMode.Value || target.Parameters.Count != arguments.Count + 1
                || target.Modes[^1] != ScalarParameterMode.Value || target.Parameters[^1] != e.Type)
                throw new Rejected(e.Location,"Accessor update does not match its setter.");
            var getMember = StructReceiverAccessor(function); var setMember = StructReceiverAccessor(e.AccessorSetter);
            var pair = getMember is not null && setMember is not null && getMember.Owner == setMember.Owner
                && getMember.AccessorMember is { } key && key.StartsWith("get:",StringComparison.Ordinal) && setMember.AccessorMember == "set:" + key[4..];
            var getter = VerifyCall(call);
            if (getter.ReturnMode != ScalarParameterMode.Value || getter.Type != e.Type || !target.Parameters.Take(arguments.Count).SequenceEqual(getter.Arguments.Select(a=>a.Type)))
                throw new Rejected(e.Location,"Accessor update getter and setter types do not agree.");
            var modes = signatures[function].Modes; var captures = getter.Arguments.ToList();
            for (var i = 0; i < arguments.Count; i++)
            {
                if (modes[i] == target.Modes[i]) continue;
                if (i != 0 || !pair || modes[i] is not (ScalarParameterMode.Ref or ScalarParameterMode.In) || target.Modes[i] is not (ScalarParameterMode.Ref or ScalarParameterMode.In))
                    throw new Rejected(e.Location,"Accessor argument passing modes do not agree.");
                if (target.Modes[i] == ScalarParameterMode.Ref) captures[i] = VerifyReferenceArgument(arguments[i],target.Parameters[i],ScalarParameterMode.Ref);
            }
            getter = getter with { Arguments = captures };
            var saved = referenceOld; var capture = nextReferenceCapture++; referenceOld = new(capture,e.Type,e.Location); Value result;
            try { result = Require(VerifyValue(e.Right,e.Location),e.Type,e.Location); } finally { referenceOld = saved; }
            Callees.Add(target.Index); return new AccessorMutation(getter,target.Index,result,capture,e.Value=="post",e.Location);
        }
    }
    private sealed partial class Emitter
    {
        private string AccessorMutationAtom(AccessorMutation mutation)
        {
            var arguments = new List<string>();(AccessorBoxReceiver Box,string Pointer,int Index)? delayed=null;
            foreach (var argument in mutation.Target.Arguments)
            {
                if(argument is AccessorBoxReceiver box)
                {
                    var pointer=ReferenceAtom(box.Receiver);delayed=(box,pointer,arguments.Count);arguments.Add("");continue;
                }
                var argumentValue = Atom(argument); var captured = Temp();
                c.Append(argument is ReferenceArgument reference ? ReferencePointer(argument.Type,reference.Mode) : CType(argument.Type)).Append(' ').Append(captured).Append(" = ").Append(argumentValue).AppendLine(";");
                arguments.Add(captured);
            }
            if(delayed is {} pending)
            {
                var boxed=Temp();c.Append(CType(pending.Box.Box)).Append(' ').Append(boxed).Append(" = ").Append(NewObject(pending.Box.Box,false)).AppendLine("();");
                c.Append(boxed).Append("->f0 = *").Append(pending.Pointer).AppendLine(";");
                var target=Temp();c.Append(CType(pending.Box.Type)).Append(' ').Append(target).Append(" = (").Append(CType(pending.Box.Type)).Append(')').Append(boxed).AppendLine(";");arguments[pending.Index]=target;
            }
            string? old = null;
            if (mutation.Capture >= 0)
            {
                if (mutation.Target.CheckReceiver) c.Append("(void)").Append(NonNull(mutation.Target.Arguments[0].Type)).Append('(').Append(arguments[0]).AppendLine(");");
                old = Temp(); c.Append(CType(mutation.Type)).Append(' ').Append(old).Append(" = ").Append(FunctionName(mutation.Target.Function)).Append('(').Append(string.Join(", ",arguments)).AppendLine(");");
                referenceOldNames.Add(mutation.Capture,old);
            }
            string value;
            try { value = Atom(mutation.Value); } finally { if (mutation.Capture >= 0) referenceOldNames.Remove(mutation.Capture); }
            if (mutation.Capture < 0 && mutation.Target.CheckReceiver) c.Append("(void)").Append(NonNull(mutation.Target.Arguments[0].Type)).Append('(').Append(arguments[0]).AppendLine(");");
            c.Append(FunctionName(mutation.Setter)).Append('(').Append(string.Join(", ",arguments.Append(value))).AppendLine(");");
            return mutation.Postfix ? old! : value;
        }
    }
}
