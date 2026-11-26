using System.Globalization;

namespace AISharp.Lowering;

using AISharp.Compiler;

public static partial class NativeProgramCompiler
{
    private sealed record StaticGet(int Owner, int Index, ScalarType Kind, SourceLocation At) : Value(At)
    { public override ScalarType Type => Kind; }
    private sealed record StoreStatic(StaticGet Root, IReadOnlyList<int> Path, Value Value, SourceLocation At) : Statement(At);

    private sealed partial class Verifier
    {
        private StaticGet VerifyStatic(ScalarExpression expression)
        {
            var parts = expression.Value?.Split(':');
            if (expression is not { Left: null, Right: null, Arguments: null } || parts?.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || owner < 0 || owner >= (layouts?.Count ?? 0) || index < 0 || index >= layouts![owner].StaticFields.Count)
                throw new Rejected(expression.Location, "Invalid static field identity.");
            var field = layouts[owner].StaticFields[index];
            if (field.Owner != owner || field.StorageType != expression.Type)
                throw new Rejected(expression.Location, "Static field type does not match its declaring type.");
            AddTypeInitialization(owner, expression.Location);
            return new(owner, index, field.StorageType, expression.Location);
        }

        private StoreStatic VerifyStaticStore(ScalarExpression root, IReadOnlyList<int> path, Value value, SourceLocation at)
        {
            var target = VerifyStatic(root);
            if (layouts![target.Owner].StaticFields[target.Index].IsReadonly && !(initializingOwner is { } current && SameTypeDeclaration(current, target.Owner)))
                throw new Rejected(root.Location, "A readonly static field cannot be assigned in this capability.");
            return new(target, path, value, at);
        }
    }

    private sealed partial class Emitter
    {
        private readonly HashSet<(int Owner, int Index)> usedStatics = [];
        private string StaticName(StaticGet field)
        {
            usedStatics.Add((field.Owner, field.Index));
            return "ai_static_" + field.Owner.ToString(CultureInfo.InvariantCulture) + "_" + field.Index.ToString(CultureInfo.InvariantCulture);
        }
    }
}
