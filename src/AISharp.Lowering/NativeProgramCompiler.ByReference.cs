using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record ReferenceArgument(Value Storage, ScalarParameterMode Mode, bool Temporary, SourceLocation At) : Value(At)
    { public override ScalarType Type => Storage.Type; }
    private sealed record ReferenceProbe(ReferenceArgument Argument, SourceLocation At) : Value(At)
    { public override ScalarType Type => ScalarType.Unit; }

    private sealed partial class Verifier
    {
        private Value VerifyReferenceProbe(ScalarExpression expression)
        {
            if (expression is not { Type: ScalarType.Unit, Left: not null, Right: null, Arguments: null }
                || expression.Value is not ("argument-ref" or "argument-out" or "argument-in")) throw new Rejected(expression.Location, "Malformed reference storage probe.");
            var mode = expression.Value switch { "argument-ref" => ScalarParameterMode.Ref, "argument-out" => ScalarParameterMode.Out, _ => ScalarParameterMode.In };
            var argument = new ScalarExpression(expression.Value, expression.Left.Type, expression.Location, Left: expression.Left);
            return new ReferenceProbe((ReferenceArgument)VerifyReferenceArgument(argument, expression.Left.Type, mode), expression.Location);
        }
        private static string? ReferencePath(ScalarExpression expression) => expression.Operation == "local" ? expression.Value
            : expression is { Operation: "field", Left: not null } && !IsClassType(expression.Left.Type) && ReferencePath(expression.Left) is { } root ? root + "." + expression.Value : null;
        private static IEnumerable<string> ReferenceLeaves(string path, ScalarType type)
        {
            if (!IsStructType(type) || IsClassType(type) || Layout(type).IsTypeParameter) { yield return path; yield break; }
            for (var i = 0; i < Layout(type).Fields.Count; i++) foreach (var leaf in ReferenceLeaves(path + "." + i, Layout(type).Fields[i].StorageType)) yield return leaf;
        }
        private bool ReferenceAssigned(ScalarExpression expression)
        {
            if (ReferencePath(expression) is not { } path) return true;
            for (var prefix = path; ;)
            {
                if (assigned.Contains(prefix)) return true;
                var dot = prefix.LastIndexOf('.'); if (dot < 0) break; prefix = prefix[..dot];
            }
            return ReferenceLeaves(path, expression.Type).All(assigned.Contains);
        }
        private void AssignReference(ScalarExpression expression)
        {
            if (ReferencePath(expression) is not { } path) return;
            assigned.Add(path); assigned.UnionWith(ReferenceLeaves(path, expression.Type));
            var root = expression; while (root.Operation == "field") root = root.Left!;
            if (root.Operation == "local" && ReferenceAssigned(root)) assigned.Add(root.Value!);
        }
        private Value VerifyReferenceArgument(ScalarExpression expression, ScalarType type, ScalarParameterMode mode, bool receiverCopy = false)
        {
            if (expression.Type != type || expression.Left is null || expression.Right is not null || expression.Arguments is not null)
                throw new Rejected(expression.Location, "Malformed reference argument.");
            var operation = expression.Operation;
            if (operation == "argument-receiver-copy" && receiverCopy && mode is ScalarParameterMode.Ref or ScalarParameterMode.In && expression.Value is "readonly" or "temporary")
                return new ReferenceArgument(Require(VerifyValue(expression.Left, expression.Location), type, expression.Location), mode, true, expression.Location);
            var expected = mode switch { ScalarParameterMode.Ref => "argument-ref", ScalarParameterMode.Out => "argument-out", _ => "argument-in" };
            if (operation == "argument-temporary" && mode is ScalarParameterMode.In or ScalarParameterMode.RefReadonly)
                return new ReferenceArgument(Require(VerifyValue(expression.Left, expression.Location), type, expression.Location), mode, true, expression.Location);
            if (operation != expected && !(mode is ScalarParameterMode.In or ScalarParameterMode.RefReadonly && operation == "argument-ref"))
                throw new Rejected(expression.Location, "Reference argument mode does not match the signature.");
            var storage = VerifyReferenceStorage(expression.Left, mode is ScalarParameterMode.Ref or ScalarParameterMode.Out || operation == "argument-ref", mode != ScalarParameterMode.Out);
            if (mode != ScalarParameterMode.Out && !ReferenceAssigned(expression.Left)) throw new Rejected(expression.Left.Location, "Reference input storage requires definite assignment.");
            return new ReferenceArgument(Require(storage, type, expression.Location), mode, false, expression.Location);
        }

        private Value VerifyReferenceStorage(ScalarExpression expression, bool writable, bool reading, bool allowPropertyValue = false)
        {
            if (expression.Operation == "ref-select") return VerifyReferenceChoice(expression, writable, reading);
            if (expression.Operation == "ref-reassign") return VerifyReferenceReassignment(expression);
            if (expression.Operation == "call")
            {
                var result = VerifyValue(expression, expression.Location);
                if (result is not Call call || call.ReturnMode == ScalarParameterMode.Value || writable && call.ReturnMode != ScalarParameterMode.Ref) throw new Rejected(expression.Location, "A reference call must preserve its return mutability.");
                return result;
            }
            if (expression.Operation == "local")
            {
                var binding = Lookup(expression, reading);
                if (writable && !binding.Mutable) throw new Rejected(expression.Location, "Reference argument requires writable storage.");
                return new Read(binding.Slot, binding.Type, expression.Location);
            }
            if (expression.Operation == "static-field")
            {
                var field = VerifyStatic(expression);
                if (layouts![field.Owner].StaticFields[field.Index].IsProperty) throw new Rejected(expression.Location, "A property is not reference storage.");
                return field;
            }
            if (expression.Operation == "field" && expression.Left is not null)
            {
                if(writable&&IsClassType(expression.Left.Type)&&Layout(expression.Left.Type).IsStringObject)
                    throw new Rejected(expression.Location,"Native string identity payloads cannot expose writable reference storage.");
                if (writable && IsClassType(expression.Left.Type) && Layout(expression.Left.Type).ArrayElementType is not null)
                    throw new Rejected(expression.Location, "Native array length storage is immutable.");
                var field = FieldOf(expression.Left.Type, expression.Value, expression.Location);
                var ownBacking = StructReceiverAccessor(currentFunctionName) is { } accessor && accessor.Backing == field.Name && accessor.Owner == (int)expression.Left.Type - ScalarLanguage.StructTypeBase;
                if (field.IsProperty && !allowPropertyValue && !ownBacking || field.StorageType != expression.Type) throw new Rejected(expression.Location, "A reference field must preserve storage identity.");
                var owner = IsClassType(expression.Left.Type) ? VerifyValue(expression.Left, expression.Location) : VerifyReferenceStorage(expression.Left, writable, false, allowPropertyValue);
                return new FieldGet(owner, int.Parse(expression.Value!, CultureInfo.InvariantCulture), field.StorageType, expression.Location);
            }
            if (expression.Operation == "array-index")
            {
                var value = (ArrayGet)VerifyArrayValue(expression, expression.Location);
                return value with { ExactElementAddress = writable && IsReferenceType(value.Type) };
            }
            throw new Rejected(expression.Location, "A reference argument requires verified variable storage.");
        }
    }

    private sealed partial class Emitter
    {
        private readonly HashSet<int> referenceSlots = [];
        private bool usesByReferenceParameters;
        private static string RawSlot(int slot) => "ai_local_" + slot.ToString(CultureInfo.InvariantCulture);
        private static string ReferencePointer(ScalarType type, ScalarParameterMode mode) => ReadonlyObjectReference(type,mode) ? "ai_readonly_object_ref" : CType(type).TrimEnd()
            + (mode is ScalarParameterMode.In or ScalarParameterMode.RefReadonly ? " const volatile *" : " volatile *");
        private static string ParameterDeclaration(ScalarType type, ScalarParameterMode mode, int slot, bool volatilePointer)
            => mode == ScalarParameterMode.Value ? CType(type).TrimEnd() + (volatilePointer ? " volatile " : " ") + RawSlot(slot)
                : ReferencePointer(type, mode) + (volatilePointer ? " volatile " : " ") + RawSlot(slot);

        private string StorageAddress(Value storage)
        {
            switch (storage)
            {
                case Read read: return "&(" + Slot(read.Slot) + ")";
                case Call call when call.ReturnMode != ScalarParameterMode.Value: return ReferenceCallAddress(call);
                case ReferenceChoice choice: return ReferenceChoiceAddress(choice);
                case ReferenceRebind rebind:
                { var address = StorageAddress(rebind.Storage); c.Append(RawSlot(rebind.Slot)).Append(" = ").Append(address).AppendLine(";"); return RawSlot(rebind.Slot); }
                case StaticGet field: EnsureType(field.Owner); return "&(" + StaticName(field) + ")";
                case FieldGet field when IsClassType(field.Operand.Type):
                {
                    var receiver = Atom(field.Operand);
                    return "&(" + NonNull(field.Operand.Type) + "(" + receiver + ")->f" + field.Index.ToString(CultureInfo.InvariantCulture) + ")";
                }
                case FieldGet field:
                    return "&((" + StorageAddress(field.Operand) + ")->f" + field.Index.ToString(CultureInfo.InvariantCulture) + ")";
                case ArrayGet element:
                {
                    var array = Atom(element.Array); var index = Atom(element.Index);
                    c.Append("if (").Append(array).AppendLine(" == NULL) ai_fault(\"System.NullReferenceException\");");
                    c.Append("if (").Append(index).Append(" < 0 || ").Append(index).Append(" >= ").Append(array).AppendLine("->f0) ai_fault(\"System.IndexOutOfRangeException\");");
                    if (element.ExactElementAddress)
                        c.Append("if (").Append(array).Append("->ai_tag != ").Append(ClassTag(element.Array.Type)).AppendLine(") ai_fault(\"System.ArrayTypeMismatchException\");");
                    return "&(" + array + "->ai_items[" + index + "])";
                }
                default: throw new InvalidOperationException("Unverified reference storage.");
            }
        }

        private string ReferenceAtom(ReferenceArgument argument)
        {
            if(ReadonlyObjectReference(argument.Type,argument.Mode))
            {
                if(!argument.Temporary)return ReadonlyObjectAddress(argument.Storage);
                var value=Atom(argument.Storage);var slot=Temp();c.Append(CType(argument.Type)).Append(' ').Append(slot).Append(" = ").Append(value).AppendLine(";");
                var reference=Temp();c.Append("ai_readonly_object_ref ").Append(reference).Append(" = ai_readonly_object_raw(&").Append(slot).AppendLine(");");return reference;
            }
            string address;
            if (argument.Temporary)
            {
                var value = Atom(argument.Storage); var temporary = Temp();
                c.Append(CType(argument.Type)).Append(' ').Append(temporary).Append(" = ").Append(value).AppendLine(";");
                address = "&" + temporary;
            }
            else address = StorageAddress(argument.Storage);
            var pointer = Temp();
            c.Append(ReferencePointer(argument.Type, argument.Mode)).Append(' ').Append(pointer).Append(" = ").Append(address).AppendLine(";");
            return pointer;
        }
    }
}
