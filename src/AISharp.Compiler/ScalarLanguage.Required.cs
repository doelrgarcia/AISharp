using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static IEnumerable<string> RequiredMemberNames(ScalarStruct info)
    {
        foreach (var field in info.Fields.Where(f => f.IsRequired && !f.Hidden)) yield return field.Name;
        for (var index = IndexOf(info.Id); index >= 0; index = activeStructs![index].BaseIndex)
            foreach (var property in activeStructs![index].Properties.Where(p => p.IsRequired)) yield return property.Name;
    }

    private sealed partial class ExtendedBinder
    {
        // The constructor is evaluated once; each initializer RHS and store/setter follows in source
        // order. This scoped expression also preserves ordering inside nested expressions and calls.
        private ScalarExpression RequiredObjectInitializer(ScalarStruct info, ScalarExpression origin, SourceLocation at, bool enforceRequired = true)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var actions = new List<ScalarExpression>();
            Local? temporary = null;
            if (Take("{"))
            {
                temporary = HiddenLocal(info.Id);
                var receiver = new ScalarExpression("local", info.Id, at, temporary.EmittedName);
                NewLines();
                while (Current.Text != "}")
                {
                    if (Current.Text == "[")
                    {
                        var target = IndexerRead(receiver); var assignment = Current.Location; Expect("=");
                        var indexedValue = Expression(); Read(indexedValue);
                        actions.Add(PropertyAssign(target, "=", indexedValue, assignment, initializing: true));
                        NewLines(); if (!Take(",")) break; NewLines(); continue;
                    }
                    var member = Next();
                    if (!IsSourceIdentifier(member.Text) || IsReserved(member.Text)) Fail("AI3106", "Expected an initializer member name.", member.Location);
                    member = member with { Text = member.Name };
                    if (!names.Add(member.Text)) Fail("AI3106", $"Member '{member.Text}' is initialized twice.", member.Location);
                    var memberReceiver=RecordMemberReceiver(receiver,member);var memberInfo=StructOf(memberReceiver.Type);
                    if(RecordMemberHiding(info)&&(FindStaticField(IndexOf(memberInfo.Id),member.Text) is not null||FindProperty(IndexOf(memberInfo.Id),member.Text) is {IsStatic:true}))Fail("AI3106","An object initializer requires instance members.",member.Location);
                    var checkedProperty = CheckedInstanceProperty(memberReceiver, member);
                    var index = checkedProperty is null ? VisibleInstanceFieldIndex(memberInfo,member.Text) : -1;
                    ScalarType type;
                    ScalarProperty? property = null;
                    if (index >= 0)
                    {
                        var field = memberInfo.Fields[index]; type = field.Type;
                        index=info.Fields.ToList().FindIndex(f=>f.Owner==field.Owner&&f.Name==field.Name);
                        if(index<0)Fail("AI3107","The initialized object does not contain its checked record member storage.",member.Location);
                        var owner = field.Owner >= 0 ? _structs[field.Owner] : info;
                        CheckAccess(owner, field.IsPublic, member.Text, member.Location);
                        if (field.IsReadonly&&!field.IsInit) Fail("AI3105", $"Member '{owner.Name}.{member.Text}' cannot be assigned in an object initializer.", member.Location);
                        if (field.WritePrivate && _struct?.Id != owner.Id) Fail("AI3119", $"The set accessor of '{owner.Name}.{member.Text}' is inaccessible.", member.Location);
                        if (field.IsEvent && _struct?.Id != owner.Id) Fail("AI3106", $"The event '{owner.Name}.{member.Text}' is unavailable in this object initializer.", member.Location);
                    }
                    else
                    {
                        property = checkedProperty ?? FindProperty(IndexOf(memberInfo.Id), member.Text);
                        if (property is null) Fail("AI3103", $"'{info.Name}' has no field or property '{member.Text}'.", member.Location);
                        var owner = _structs[property!.Owner]; type = property.Type;
                        CheckAccess(owner, property.IsPublic, member.Text, member.Location);
                        if (property.IsEvent) Fail("AI3106", "An event accessor cannot be used as an initializer property.", member.Location);
                        if (property.IsStatic) Fail("AI3106", "An object initializer requires instance members.", member.Location);
                        if (!property.HasSet && property.ReferenceMode == ScalarParameterMode.Value) Fail("AI3105", $"Property '{owner.Name}.{member.Text}' is read only.", member.Location);
                        if (property.SetPrivate && _struct?.Id != owner.Id) Fail("AI3119", $"The set accessor of '{owner.Name}.{member.Text}' is inaccessible.", member.Location);
                        if (property.Setter is null && property.ReferenceMode == ScalarParameterMode.Value)
                        {
                            index = info.Fields.ToList().FindIndex(f => f.Name == property.Backing && f.Hidden&&f.Owner==property.Owner);
                            if (index < 0) Fail("AI3106", "An initializer property has no setter or backing storage.", member.Location);
                        }
                    }
                    Expect("=");
                    var item = Expression(); Read(item); item = Coerce(item, type);
                    if (property is { ReferenceMode: not ScalarParameterMode.Value })
                    {
                        var target = PropertyRead(memberReceiver, property, member); Write(target);
                        actions.Add(ReferenceWrite(target, item, member.Location));
                    }
                    else actions.Add(property?.Setter is { } setter
                        ? AccessorCall(memberReceiver, property, setter, [item], member.Location)
                        : new("init", info.Id, member.Location, index.ToString(CultureInfo.InvariantCulture), Left: receiver) { Arguments = [item] });
                    NewLines();
                    if (!Take(",")) break;
                    NewLines();
                }
                NewLines(); Expect("}");
            }
            var missing = enforceRequired ? RequiredMemberNames(info).Distinct(StringComparer.Ordinal).Where(n => !names.Contains(n)).ToArray() : [];
            if (missing.Length > 0) Fail("AI3120", $"Creation of '{info.Name}' must initialize required member(s): {string.Join(", ", missing)}.", at);
            return actions.Count == 0 ? origin : new("init-members", info.Id, at, temporary!.EmittedName, Left: origin) { Arguments = actions };
        }
    }
}
