using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static HashSet<(int Owner, string Backing)>? staticPropertyFieldUses;
    [ThreadStatic] private static List<(int Owner, string Backing, SourceLocation At)>? staticPropertyConstructorWrites;

    private static int StaticPropertyDeclaration(ScalarStruct owner) => owner.Template >= 0 ? owner.Template : IndexOf(owner.Id);
    private static (int Owner, string Backing) StaticPropertyKey(ScalarStruct owner, string backing) => (StaticPropertyDeclaration(owner), backing);

    // Headers reserve candidate storage before accessor bodies are bound. Resolve eligibility from
    // actual keyword binding (including closures), not a textual token that may name a member/type.
    private static void ValidateStaticPropertyBacking()
    {
        var proven = new HashSet<(int Owner, string Backing)>(staticPropertyFieldUses!);
        for (var k = 0; k < activeStructs!.Count; k++)
        {
            var type = activeStructs[k];
            foreach (var property in type.Properties)
                if (property.IsStatic && property.Backing is { } backing
                    && (property.HasGet && property.Getter is null || property.HasSet && property.Setter is null))
                    proven.Add(StaticPropertyKey(property.Owner >= 0 ? activeStructs[property.Owner] : type, backing));
        }
        for (var k = 0; k < activeStructs.Count; k++)
        {
            var type = activeStructs[k];
            foreach (var property in type.Properties)
            {
                if (!property.IsStatic || property.Backing is not { } backing || property.Owner >= 0 && property.Owner != k) continue;
                if (!proven.Contains(StaticPropertyKey(type, backing)) && type.StaticInitializers.Any(i => i.Field == backing))
                    throw DeclarationError("AI3106", "Only a field-backed property can have an initializer.", property.Location);
            }
        }
        foreach (var write in staticPropertyConstructorWrites!)
            if (!proven.Contains((write.Owner, write.Backing)))
                throw DeclarationError("AI3105", "A computed get-only property cannot be assigned in a static constructor.", write.At);
        for (var k = 0; k < activeStructs.Count; k++)
        {
            var type = activeStructs[k];
            activeStructs[k] = type with
            {
                Properties = type.Properties.Select(p => p.IsStatic && p.Backing is { } backing
                    && !proven.Contains(StaticPropertyKey(p.Owner >= 0 ? activeStructs[p.Owner] : type, backing)) ? p with { Backing = null } : p).ToList(),
                Methods = type.Methods.Select(m => m.IsStatic && m.IsAccessor && m.Backing is { } backing
                    && !proven.Contains(StaticPropertyKey(m.Owner >= 0 ? activeStructs[m.Owner] : type, backing)) ? m with { Backing = null } : m).ToList()
            };
        }
    }

    private sealed partial class ExtendedBinder
    {
        private ScalarExpression PropertyBackingRead(ScalarExpression receiver, ScalarProperty property, SourceLocation at)
        {
            if (property.Backing is null) Fail("AI3106", "This property has no backing storage.", at);
            if (property.IsStatic)
                return StaticFieldRead(_structs[property.Owner], new(property.Backing!, at), allowHidden: true);
            var index = StructOf(receiver.Type).Fields.ToList().FindIndex(f => f.Name == property.Backing&&(property.Owner<0||f.Owner==property.Owner));
            return new("field", property.Type, at, index.ToString(CultureInfo.InvariantCulture), Left: receiver);
        }
    }
}
