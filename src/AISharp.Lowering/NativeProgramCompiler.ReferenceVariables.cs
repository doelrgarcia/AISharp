using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record ReferenceChoice(Value Condition, Value Yes, Value No, bool Writable, SourceLocation At) : Value(At)
    { public override ScalarType Type => Yes.Type; }
    private sealed record ReferenceReturn(Value Storage, SourceLocation At) : Value(At)
    { public override ScalarType Type => Storage.Type; }
    private sealed record ReferenceRebind(int Slot, Value Storage, ScalarType Kind, SourceLocation At) : Value(At)
    { public override ScalarType Type => Kind; }
    private sealed record ReferenceDeclare(int Slot, ScalarType Kind, ScalarParameterMode Mode, Value? Storage, SourceLocation At) : Statement(At);
    private sealed record ReferenceStore(Value Storage, Value Value, SourceLocation At) : Statement(At);

    private sealed partial class Verifier
    {
        private int referenceDepth = 1;
        private ScalarParameterMode returnMode;
        private int ReferenceContext(ScalarExpression expression)
        {
            if (expression.Operation == "local") return Lookup(expression, false).Context;
            if (expression.Operation is "static-field" or "array-index") return 0;
            if (expression is { Operation: "field", Left: not null }) return IsClassType(expression.Left.Type) ? 0 : ReferenceContext(expression.Left);
            if (expression is { Operation: "ref-select", Arguments: { } arms }) return arms.Max(ReferenceContext);
            if (expression.Operation == "ref-reassign") return ReferenceContext(expression.Left!);
            if (expression.Operation == "call" && expression.Value is { } callee && signatures.TryGetValue(callee, out var signature))
            {
                var contexts = (expression.Arguments ?? []).Select((a,i) => (Argument:a,Index:i))
                    .Where(p => signature.Modes[p.Index] != ScalarParameterMode.Value && signature.Modes[p.Index] != ScalarParameterMode.Out && !signature.Scoped[p.Index])
                    .Select(p => p.Argument.Operation == "argument-temporary" ? referenceDepth : ReferenceContext(p.Argument.Left!));
                return contexts.DefaultIfEmpty(0).Max();
            }
            throw new Rejected(expression.Location, "A reference requires a verified lifetime-bearing location.");
        }
        private bool VerifyReferenceDeclaration(ScalarStatement statement, List<Statement> output, List<string> declared)
        {
            if(statement.Kind=="ref-local-slot")
            {
                if(statement.Name is null||bindings.ContainsKey(statement.Name)||statement.Expression is not null||statement.ReferenceMode is not (ScalarParameterMode.Ref or ScalarParameterMode.RefReadonly))throw new Rejected(statement.Location,"Malformed ephemeral reference slot.");
                var type=Map(statement.Type,statement.Location);var next=nextSlot++;
                bindings.Add(statement.Name,new(next,type,statement.ReferenceMode==ScalarParameterMode.Ref,null){Mode=statement.ReferenceMode,Context=referenceDepth});declared.Add(statement.Name);
                output.Add(new ReferenceDeclare(next,type,statement.ReferenceMode,null,statement.Location));return true;
            }
            if (statement.Name is null || bindings.ContainsKey(statement.Name) || statement.Expression is null
                || statement.ReferenceMode is not (ScalarParameterMode.Ref or ScalarParameterMode.RefReadonly)) throw new Rejected(statement.Location, "Malformed reference local.");
            var value = Require(VerifyReferenceStorage(statement.Expression, statement.ReferenceMode == ScalarParameterMode.Ref, true), Map(statement.Type, statement.Location), statement.Location);
            var context = ReferenceContext(statement.Expression);if(statement.Scoped)context=Math.Max(context,referenceDepth);
            var slot=nextSlot++;bindings.Add(statement.Name,new(slot,statement.Type,statement.ReferenceMode==ScalarParameterMode.Ref,null){Mode=statement.ReferenceMode,Context=context});
            declared.Add(statement.Name);assigned.Add(statement.Name);output.Add(new ReferenceDeclare(slot,statement.Type,statement.ReferenceMode,value,statement.Location));return true;
        }
        private Value VerifyReferenceReassignment(ScalarExpression expression)
        {
            if (expression.Left is null || expression.Right is null) throw new Rejected(expression.Location,"Malformed reference reassignment.");
            var binding=Lookup(expression.Left,false);
            if(binding.Mode is not (ScalarParameterMode.Ref or ScalarParameterMode.Out or ScalarParameterMode.In or ScalarParameterMode.RefReadonly))throw new Rejected(expression.Location,"Only a reference binding can be ref-reassigned.");
            if(binding.Mode==ScalarParameterMode.Out)Lookup(expression.Left,true);
            var value=Require(VerifyReferenceStorage(expression.Right,binding.Mode is ScalarParameterMode.Ref or ScalarParameterMode.Out,true),binding.Type,expression.Location);
            if(ReferenceContext(expression.Right)>binding.Context)throw new Rejected(expression.Location,"A reference reassignment cannot narrow its referent lifetime.");
            assigned.Add(expression.Left.Value!);return new ReferenceRebind(binding.Slot,value,binding.Type,expression.Location);
        }
        private Value VerifyReferenceChoice(ScalarExpression expression,bool writable,bool reading)
        {
            if(expression.Left is null||expression.Arguments is not {Count:2} arms)throw new Rejected(expression.Location,"Malformed reference conditional.");
            var condition=Require(VerifyValue(expression.Left,expression.Location),ScalarType.Bool,expression.Location);var before=new HashSet<string>(assigned,StringComparer.Ordinal);
            var yes=Require(VerifyReferenceStorage(arms[0],writable,reading),expression.Type,expression.Location);var afterYes=assigned;assigned=new(before,StringComparer.Ordinal);
            var no=Require(VerifyReferenceStorage(arms[1],writable,reading),expression.Type,expression.Location);
            if(Constant(condition)==true)assigned=afterYes;else if(Constant(condition)!=false)assigned.IntersectWith(afterYes);
            return new ReferenceChoice(condition,yes,no,writable,expression.Location);
        }
        private Value VerifyReferenceReturn(ScalarExpression expression)
        {
            if(returnMode==ScalarParameterMode.Value||expression.Left is null)throw new Rejected(expression.Location,"Reference return does not match the signature.");
            var value=Require(VerifyReferenceStorage(expression.Left,returnMode==ScalarParameterMode.Ref,true),returnType,expression.Location);
            if(ReferenceContext(expression.Left)>1)throw new Rejected(expression.Location,"A reference return cannot escape local or scoped storage.");
            return new ReferenceReturn(value,expression.Location);
        }
    }

    private sealed partial class Emitter
    {
        private ScalarParameterMode currentReturnMode;
        private static bool HasReferenceLocals(IEnumerable<Statement> statements)=>statements.Any(s=>s switch
        {ReferenceDeclare=>true,Block b=>HasReferenceLocals(b.Statements),If i=>HasReferenceLocals(i.Then)||HasReferenceLocals(i.Else),Loop l=>HasReferenceLocals(l.Body)||HasReferenceLocals(l.Step),Try t=>HasReferenceLocals(t.Body)||t.Catches.Any(h=>HasReferenceLocals(h.Body))||t.Finally is not null&&HasReferenceLocals(t.Finally),SwitchBlock b=>HasReferenceLocals(b.Body),_=>false});
        private string ReferenceCallAddress(Call call)
        {
            var arguments=call.Arguments.Select(Atom).ToList();var pointer=Temp();
            if(call.CheckReceiver)c.Append("(void)").Append(NonNull(call.Arguments[0].Type)).Append('(').Append(arguments[0]).AppendLine(");");
            c.Append(ReferencePointer(call.Kind,call.ReturnMode)).Append(' ').Append(pointer).Append(" = ").Append(FunctionName(call.Function)).Append('(').Append(string.Join(", ",arguments)).AppendLine(");");return pointer;
        }
        private string ReferenceChoiceAddress(ReferenceChoice choice)
        {
            var guard=Guard(choice.Condition);var pointer=Temp();c.Append(ReferencePointer(choice.Type,choice.Writable?ScalarParameterMode.Ref:ScalarParameterMode.RefReadonly)).Append(' ').Append(pointer).AppendLine(";");
            var descriptor=ReadonlyObjectReference(choice.Type,choice.Writable?ScalarParameterMode.Ref:ScalarParameterMode.RefReadonly);
            c.Append("if (").Append(guard).AppendLine(") {");var yes=descriptor?ReadonlyObjectAddress(choice.Yes):StorageAddress(choice.Yes);c.Append(pointer).Append(" = ").Append(yes).AppendLine(";");
            c.AppendLine("} else {");var no=descriptor?ReadonlyObjectAddress(choice.No):StorageAddress(choice.No);c.Append(pointer).Append(" = ").Append(no).AppendLine(";");c.AppendLine("}");return pointer;
        }
    }
}
