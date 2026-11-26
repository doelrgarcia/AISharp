namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<(int Function,string Scope,int Call),int>? checkedRecordMemberReceivers;
    private static bool OwnRecordMember(ScalarStruct owner,string name)
        =>owner.Fields.Any(f=>f.Owner==IndexOf(owner.Id)&&!f.Hidden&&f.Name==name)
        ||owner.StaticFields.Any(f=>!f.Hidden&&f.Name==name)
        ||owner.Properties.Any(p=>!p.IsIndexer&&p.InterfaceQualifier is null&&p.Name==name)
        ||owner.Methods.Any(m=>!m.IsConstructor&&!m.IsAccessor&&m.Name==name);
    private static int VisibleInstanceFieldIndex(ScalarStruct owner,string name)
    {
        var fields=owner.Fields.ToList();
        if(!RecordMemberHiding(owner))return fields.FindLastIndex(f=>!f.Hidden&&f.Name==name);
        for(var k=IndexOf(owner.Id);k>=0;k=activeStructs![k].BaseIndex)
        {
            var index=fields.FindLastIndex(f=>f.Owner==k&&!f.Hidden&&f.Name==name);
            if(index>=0)return index;
            if(OwnRecordMember(activeStructs![k],name))return -1;
        }
        return -1;
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarStruct RecordLookupOwner(ScalarStruct info,string name)
        {
            if(!RecordMemberHiding(info))return info;
            var skipped=false;
            for(var k=IndexOf(info.Id);k>=0;k=_structs[k].BaseIndex)
            {
                var owner=_structs[k];
                var access=owner.Fields.Where(f=>f.Owner==k&&!f.Hidden&&f.Name==name).Select(f=>f.IsPublic)
                    .Concat(owner.StaticFields.Where(f=>!f.Hidden&&f.Name==name).Select(f=>f.IsPublic))
                    .Concat(owner.Properties.Where(p=>!p.IsIndexer&&p.InterfaceQualifier is null&&p.Name==name).Select(p=>p.IsPublic))
                    .Concat(owner.Methods.Where(m=>!m.IsConstructor&&!m.IsAccessor&&m.Name==name).Select(m=>m.IsPublic)).ToArray();
                if(access.Length==0)continue;
                if(access.Any(v=>v)||SameTypeScope(owner))return skipped?owner:info;
                skipped=true;
            }
            return info;
        }
        private ScalarExpression AccessibleRecordReceiver(ScalarExpression receiver,Lexeme member)
        {
            var selected=RecordLookupOwner(StructOf(receiver.Type),member.Text);
            return receiver.Operation=="static-type"?receiver with{Type=selected.Id}:Upcast(receiver,selected.Id);
        }
        private ScalarExpression RecordMemberReceiver(ScalarExpression receiver,Lexeme member)
        {
            if(!Has(ScalarFeatures.RecordInheritance))return receiver;
            if(receiver.Operation=="static-type")return AccessibleRecordReceiver(receiver,member);
            var info=StructOf(receiver.Type);var site=DeconstructBindingSite(member.Location);
            if(_checkOnly&&info.IsTypeParameter)
            {
                for(var k=info.BaseIndex;k>=0;k=_structs[k].BaseIndex)
                {
                    var owner=_structs[k];if(!owner.IsRecord)continue;
                    var accessible=owner.Fields.Any(f=>f.Name==member.Text&&!f.Hidden&&f.IsPublic)||owner.Properties.Any(p=>p.Name==member.Text&&p.IsPublic)||owner.Methods.Any(m=>m.Name==member.Text&&m.IsPublic);
                    if(!accessible)continue;
                    checkedRecordMemberReceivers![site]=owner.Template>=0?owner.Template:k;
                    return AccessibleRecordReceiver(Upcast(receiver,owner.Id),member);
                }
            }
            else if(!_checkOnly&&(_typeArgs is not null||_struct?.TypeArguments is not null)&&checkedRecordMemberReceivers!.TryGetValue(site,out var template))
            {
                for(var k=IndexOf(info.Id);k>=0;k=_structs[k].BaseIndex)
                    if((_structs[k].Template>=0?_structs[k].Template:k)==template)return AccessibleRecordReceiver(Upcast(receiver,_structs[k].Id),member);
                Fail("AI3107","The closed receiver does not preserve its checked record member declaration.",member.Location);
            }
            return AccessibleRecordReceiver(receiver,member);
        }
        private bool RecordDelegateHidesMethod(ScalarType type,string name)
        {
            if(!Has(ScalarFeatures.RecordInheritance)||!StructOf(type).IsRecord)return false;
            for(var k=IndexOf(type);k>=0;k=_structs[k].BaseIndex)
            {
                var owner=_structs[k];
                if(owner.Methods.Any(m=>m.Name==name&&!m.IsAccessor&&!m.IsConstructor))return false;
                if(owner.Fields.Any(f=>f.Owner==k&&!f.Hidden&&f.Name==name&&IsDelegate(f.Type))||owner.StaticFields.Any(f=>!f.Hidden&&f.Name==name&&IsDelegate(f.Type))||owner.Properties.Any(p=>p.Name==name&&IsDelegate(p.Type)))return true;
            }
            return false;
        }
    }
}
