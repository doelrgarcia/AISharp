namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<int, ScalarStructMethod>? staticInterfaceCalls;

    private static bool ValidStaticInterfaceArgument(ScalarType argument)
    {
        if ((int)argument < StructTypeBase || IndexOf(argument) >= activeStructs!.Count) return true;
        var all = activeStructs!; var info = all[IndexOf(argument)];
        if (!info.IsInterface || info.IsTypeParameter) return true;
        foreach (var index in new[] { IndexOf(argument) }.Concat(InterfaceClosure(all, info.Interfaces)).Distinct())
            foreach (var root in all[index].Methods.Where(m => m.IsStatic && m.IsPublic && m.Slot == m.QualifiedName))
                if (ResolveInterfaceImplementation(IndexOf(argument), root) is not { Ambiguous: false, Method: { IsAbstract: false } }) return false;
        return true;
    }

    private sealed partial class ExtendedBinder
    {
        private (ScalarStructMethod Root, ScalarStructMethod Implementation) StaticInterfaceMember(ScalarExpression receiver, Lexeme member)
        {
            var type = receiver.Type; var info = StructOf(type); ScalarStructMethod root;
            if (info.IsTypeParameter)
            {
                var candidates = InterfaceMethods(info.Interfaces, member.Text).Where(m => m.IsStatic && m.IsPublic && m.Slot is not null).ToArray();
                if (candidates.Length != 1) Fail("AI3106", candidates.Length == 0 ? "A static type-parameter call requires a static abstract or virtual interface constraint." : $"The static call to '{member.Text}' is ambiguous between interface constraints.", member.Location);
                root = candidates[0]; staticInterfaceCalls![member.Location.Offset] = root;
            }
            else if (!staticInterfaceCalls!.TryGetValue(member.Location.Offset, out root!))
            { Fail("AI3106", "The static interface call has no checked declaration binding.", member.Location); return default!; }
            root = ReboundInterfaceRoot(root);
            var implementation = root;
            if (!info.IsTypeParameter)
            {
                if (!Derives(_structs, IndexOf(type), root.Owner)) Fail("AI3107", "The type argument does not satisfy its checked static interface proof.", member.Location);
                var resolution = ResolveInterfaceImplementation(IndexOf(type), root);
                if (resolution.Ambiguous || resolution.Method is not { IsStatic: true, IsAbstract: false }) Fail("AI3107", "The type argument has no unique static interface implementation.", member.Location);
                implementation = resolution.Method!;
            }
            return (root, implementation);
        }

        private ScalarExpression StaticInterfaceCall(ScalarExpression receiver, Lexeme member, List<ScalarExpression>? given)
        {
            var (root, implementation) = StaticInterfaceMember(receiver, member);
            List<(ScalarType Type, SourceLocation Location)>? explicitArguments = null;
            if (Take("<"))
            {
                explicitArguments = [];
                do { var argument = TypeToken(); explicitArguments.Add((Type(argument.Text, argument.Location), argument.Location)); } while (Take(","));
                Expect(">");
            }
            var arguments = given ?? new List<ScalarExpression>();
            if (given is null)
            {
                Expect("(");
                if (!Take(")")) { do { var value = Expression(); Read(value); arguments.Add(value); } while (Take(",")); Expect(")"); }
            }
            if (GenericOf(root.Declaration) is { } generic)
                return GenericCall(member, root.Declaration, generic, explicitArguments, arguments, _structs[root.Owner], root, receiver, true, implementation);
            if (explicitArguments is not null) Fail("AI3116", "This static interface member does not accept method type arguments.", member.Location);
            if (arguments.Count != root.Declaration.Parameters.Count) Fail("AI3116", $"'{root.QualifiedName}' expects {root.Declaration.Parameters.Count} argument(s) but {arguments.Count} were supplied.", member.Location);
            for (var i = 0; i < arguments.Count; i++) arguments[i] = Coerce(arguments[i], Type(root.Declaration.Parameters[i].Type, member.Location));
            return InterfaceImplementationCall(implementation, null, arguments, Type(root.Declaration.ReturnType, member.Location, true), member.Location);
        }
    }
}
