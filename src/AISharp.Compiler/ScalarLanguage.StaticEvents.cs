using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private ScalarExpression? StaticEventUpdate(ScalarExpression target, string op, ScalarExpression value, SourceLocation at)
        {
            if (target.Operation != "static-field" || op is not ("+=" or "-=")) return null;
            var parts = target.Value!.Split(':');
            var owner = _structs[int.Parse(parts[0], CultureInfo.InvariantCulture)];
            var field = owner.StaticFields[int.Parse(parts[1], CultureInfo.InvariantCulture)];
            if (!field.IsEvent) return null;
            Write(target);
            var handler = Coerce(value, target.Type);
            var helper = DelegateOperationCall(target.Type, op == "+=" ? "combine" : "remove", target, handler, at).Value;
            // The accessor argument is evaluated once before initialization and the atomic update.
            return new("static-event-update", ScalarType.Unit, at, helper, Left: target, Right: handler);
        }
    }
}
