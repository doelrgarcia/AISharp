using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record AssignedValue(Statement Store,ScalarType Kind,SourceLocation At):Value(At){public override ScalarType Type=>Kind;}
    private sealed partial class Verifier
    {
        public bool HasValueUpdates { get; private set; }
        private Value VerifyAssignedValue(ScalarExpression e)
        {
            if(e.Left is null||e.Right is null||e.Arguments is not null||e.Type!=e.Left.Type||e.Value is not (null or "compound"))throw new Rejected(e.Location,"Malformed assignment value.");
            HasValueUpdates=true;
            return new AssignedValue(VerifyUpdate(e,e.Location),e.Type,e.Location);
        }
        private Value VerifyIncrementValue(ScalarExpression e)
        {
            if(e.Left is null||e.Right is null||e.Arguments is not null||e.Value is not null||e.Type!=e.Left.Type)throw new Rejected(e.Location,"Malformed increment value.");
            HasValueUpdates=true;
            return VerifyReferenceMutation(e with {Operation="reference-update",Value=e.Operation.StartsWith("post",StringComparison.Ordinal)?"post":"pre"});
        }
    }
    private sealed partial class Emitter
    {
        private string AssignedValueAtom(AssignedValue assignment)
        {
            string Copy(Value value)
            {
                var evaluated=Atom(value);var copy=Temp();c.Append(CType(assignment.Type)).Append(' ').Append(copy).Append(" = ").Append(evaluated).AppendLine(";");return copy;
            }
            switch(assignment.Store)
            {
                case Store store:
                {
                    var value=Copy(store.Value);c.Append(Slot(store.Slot)).Append(" = ").Append(value).AppendLine(";");return value;
                }
                case StoreField field:
                {
                    var value=Copy(field.Value);c.Append(Slot(field.Slot));foreach(var index in field.Path)c.Append(".f").Append(index.ToString(CultureInfo.InvariantCulture));
                    c.Append(" = ").Append(value).AppendLine(";");return value;
                }
                case StoreStatic field:
                {
                    if(field.Path.Count>0)EnsureType(field.Root.Owner);var value=Copy(field.Value);if(field.Path.Count==0)EnsureType(field.Root.Owner);
                    if(IsStaticEvent(field.Root)&&field.Path.Count==0)c.Append("(void)InterlockedExchangePointer((PVOID volatile *)&").Append(StaticName(field.Root)).Append(", (PVOID)").Append(value).AppendLine(");");
                    else {c.Append(StaticName(field.Root));foreach(var index in field.Path)c.Append(".f").Append(index.ToString(CultureInfo.InvariantCulture));c.Append(" = ").Append(value).AppendLine(";");}
                    return value;
                }
                case StoreRef store:
                {
                    var evaluated=Atom(store.Target);var receiver=Temp();c.Append(CType(store.Target.Type)).Append(' ').Append(receiver).Append(" = ").Append(evaluated).AppendLine(";");
                    if(store.Path.Count>1)c.Append("(void)").Append(NonNull(store.Target.Type)).Append('(').Append(receiver).AppendLine(");");
                    if(store.ReceiverCapture>=0)receiverNames.Add(store.ReceiverCapture,receiver);
                    string value;try{value=Copy(store.Value);}finally{if(store.ReceiverCapture>=0)receiverNames.Remove(store.ReceiverCapture);}
                    c.Append(NonNull(store.Target.Type)).Append('(').Append(receiver).Append(")->f").Append(string.Join(".f",store.Path.Select(i=>i.ToString(CultureInfo.InvariantCulture)))).Append(" = ").Append(value).AppendLine(";");return value;
                }
                default:throw new InvalidOperationException("Unverified assignment value storage.");
            }
        }
    }
}
