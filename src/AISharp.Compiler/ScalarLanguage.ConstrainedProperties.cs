namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<int, ScalarProperty>? constrainedInstanceProperties;
    [ThreadStatic] private static Dictionary<int, ScalarProperty>? constrainedIndexers;

    private sealed partial class ExtendedBinder
    {
        private ScalarProperty ReboundProperty(ScalarProperty property)
        {
            var original = _structs[property.Owner];
            var method = original.Methods.First(m => m.QualifiedName == (property.Getter ?? property.Setter));
            var rebound = ReboundInterfaceRoot(method);
            return _structs[rebound.Owner].Properties.First(p => p.Getter == rebound.QualifiedName || p.Setter == rebound.QualifiedName);
        }
        private ScalarProperty? CheckedIndexer(ScalarExpression receiver, SourceLocation at)
        {
            if (StructOf(receiver.Type).IsTypeParameter || !constrainedIndexers!.TryGetValue(at.Offset,out var property)) return null;
            property = ReboundProperty(property);
            if (!Derives(_structs,IndexOf(receiver.Type),property.Owner)) Fail("AI3107","The type argument does not satisfy its checked indexer contract.",at);
            return property;
        }
        // Preserve the checked interface member when a generic body is specialized. A
        // concrete type may implement it explicitly or have a separate public namesake.
        private ScalarProperty? CheckedInstanceProperty(ScalarExpression receiver, Lexeme member)
        {
            var info = StructOf(receiver.Type);
            ScalarProperty? property;
            if (info.IsTypeParameter)
            {
                property = FindProperty(IndexOf(info.Id),member.Text);
                if (property is null || property.IsStatic || !_structs[property.Owner].IsInterface) return null;
                constrainedInstanceProperties![member.Location.Offset] = property;
            }
            else if (!constrainedInstanceProperties!.TryGetValue(member.Location.Offset,out property)) return null;
            property = ReboundProperty(property);
            if (!info.IsTypeParameter && !Derives(_structs,IndexOf(info.Id),property.Owner)) Fail("AI3107","The type argument does not satisfy its checked property contract.",member.Location);
            return property;
        }
    }
}
