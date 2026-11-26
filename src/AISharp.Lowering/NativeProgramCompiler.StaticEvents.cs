using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record StaticEventUpdate(StaticGet Root, Value Handler, int Function, SourceLocation At) : Value(At)
    { public override ScalarType Type => ScalarType.Unit; }

    private static bool IsStaticEvent(StaticGet field) => layouts![field.Owner].StaticFields[field.Index].IsEvent;

    private sealed partial class Verifier
    {
        private StaticEventUpdate VerifyStaticEventUpdate(ScalarExpression expression)
        {
            if (expression is not { Type: ScalarType.Unit, Left.Operation: "static-field", Right: not null, Arguments: null })
                throw new Rejected(expression.Location, "Invalid static event update.");
            var target = VerifyStatic(expression.Left);
            if (!IsStaticEvent(target) || !IsClassType(target.Kind) || layouts![target.Owner].StaticFields[target.Index].IsReadonly)
                throw new Rejected(expression.Location, "An event update requires writable delegate storage.");
            var prefix = "__" + Layout(target.Kind).Name;
            var helperName = expression.Value ?? throw new Rejected(expression.Location, "An event update requires a delegate helper.");
            var generated = Layout(target.Kind) is { IsDelegate: true, Template: >= 0 }
                && System.Text.RegularExpressions.Regex.IsMatch(helperName, @"^__delegate_operation<[0-9]+>$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!generated && helperName != prefix + "_combine" && helperName != prefix + "_remove")
                throw new Rejected(expression.Location, "An event update requires its delegate combine or remove helper.");
            if (!signatures.TryGetValue(helperName, out var helper) || helper.Parameters.Count != 2
                || helper.Parameters.Any(p => p != target.Kind) || helper.Return != target.Kind)
                throw new Rejected(expression.Location, "An event helper must preserve the event delegate type.");
            var handler = Require(VerifyValue(expression.Right, expression.Location), target.Kind, expression.Right.Location);
            Callees.Add(helper.Index);
            return new(target, handler, helper.Index, expression.Location);
        }
    }

    private sealed partial class Emitter
    {
        private static string AtomicEventLoad(StaticGet field, string name) => "(" + CType(field.Kind)
            + ")InterlockedCompareExchangePointer((PVOID volatile *)&" + name + ", NULL, NULL)";

        private string EmitStaticEventUpdate(StaticEventUpdate update)
        {
            var value = Atom(update.Handler); var handler = Temp(); var old = Temp(); var next = Temp(); var observed = Temp();
            var type = CType(update.Root.Kind);
            c.Append(type).Append(' ').Append(handler).Append(" = ").Append(value).AppendLine(";");
            EnsureType(update.Root.Owner);
            var name = StaticName(update.Root);
            c.Append(type).Append(' ').Append(old).Append(" = ").Append(AtomicEventLoad(update.Root, name)).AppendLine(";");
            c.AppendLine("for (;;) {");
            c.Append(type).Append(' ').Append(next).Append(" = ").Append(FunctionName(update.Function)).Append('(').Append(old).Append(", ").Append(handler).AppendLine(");");
            c.Append(type).Append(' ').Append(observed).Append(" = (").Append(type).Append(")InterlockedCompareExchangePointer((PVOID volatile *)&").Append(name).Append(", (PVOID)").Append(next).Append(", (PVOID)").Append(old).AppendLine(");");
            c.Append("if (").Append(observed).Append(" == ").Append(old).AppendLine(") break;");
            c.Append(old).Append(" = ").Append(observed).AppendLine(";\n}");
            return "0";
        }
    }
}
