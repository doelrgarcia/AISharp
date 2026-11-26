namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static ScalarExpression AccessorReferenceResult(ScalarExpression value, ScalarStructMethod method)
        => method.ReceiverMode == ScalarParameterMode.Value ? value : value with { ReferenceExcludedArguments = [0, .. value.ReferenceExcludedArguments ?? []] };

    private sealed partial class ExtendedBinder
    {
        private bool WritableAccessorReceiver(ScalarExpression receiver)
        {
            if (receiver.Operation == "local" && _hiddenLocals.ContainsKey(receiver.Value!)) return !_patternSubjectWritable.TryGetValue(receiver.Value!,out var writable)||writable;
            if (!IsReferenceStorage(receiver)) return false;
            try { Write(receiver); return true; }
            catch (ScalarError error) when (error.Diagnostic.Code is "AI3105" or "AI3107") { return false; }
        }
        private bool PropertyValueReceiver(ScalarExpression receiver)
        {
            if (_propertyReads.ContainsKey(receiver) && receiver.ReferenceMode == ScalarParameterMode.Value) return true;
            if (receiver.Operation == "field")
            {
                var field = StructOf(receiver.Left!.Type).Fields[int.Parse(receiver.Value!, System.Globalization.CultureInfo.InvariantCulture)];
                return field.IsProperty || !IsClass(receiver.Left.Type) && PropertyValueReceiver(receiver.Left);
            }
            if (receiver.Operation == "static-field")
            {
                var parts = receiver.Value!.Split(':'); return _structs[int.Parse(parts[0])].StaticFields[int.Parse(parts[1])].IsProperty;
            }
            return false;
        }
        private ScalarExpression AccessorReceiverArgument(ScalarStructMethod method, ScalarExpression receiver, SourceLocation at)
        {
            var owner = _structs[method.Owner]; receiver = Upcast(receiver, owner.Id);
            if (method.ReceiverMode == ScalarParameterMode.Value) return receiver;
            var setter = method.AccessorMember?.StartsWith("set:", System.StringComparison.Ordinal) == true;
            var boxedBridge=method.IsLifted&&method.Name.StartsWith("$boxed_accessor<",System.StringComparison.Ordinal);
            if (setter && !boxedBridge && method.ReceiverMode==ScalarParameterMode.Ref && PropertyValueReceiver(receiver)) Fail("AI3105", "A value returned by a property is not a writable struct receiver.", at);
            var storage = IsReferenceStorage(receiver); var writable = method.ReceiverMode == ScalarParameterMode.Ref && storage && WritableAccessorReceiver(receiver);
            if (method.ReceiverMode == ScalarParameterMode.Ref && writable)
                return new("argument-ref", receiver.Type, at, "forwarded", Left: receiver);
            if (method.ReceiverMode == ScalarParameterMode.Ref && setter&&!boxedBridge)
                Fail("AI3105", "A mutating struct setter requires writable variable storage.", at);
            if (method.ReceiverMode == ScalarParameterMode.In && storage)
                return new("argument-in", receiver.Type, at, "forwarded", Left: receiver);
            return new("argument-receiver-copy", receiver.Type, at, storage ? "readonly" : "temporary", Left: receiver);
        }
        private static ScalarExpression CaptureAccessorReceiver(ScalarExpression getter, ScalarExpression setter)
        {
            if (getter.Arguments is not { Count: > 0 } ga || setter.Arguments is not { Count: > 0 } sa) return getter;
            if (ga[0].Operation == "argument-in" && sa[0].Operation == "argument-ref")
                return getter with { Arguments = [sa[0], .. ga.Skip(1)] };
            return getter;
        }
    }
}
