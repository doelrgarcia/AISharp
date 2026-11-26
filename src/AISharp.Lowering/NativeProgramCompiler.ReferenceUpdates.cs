using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record ReferenceOld(int Capture,ScalarType Kind,SourceLocation At):Value(At){public override ScalarType Type=>Kind;}
    private sealed record ReferenceMutation(Value Storage,Value Value,int Capture,bool Postfix,SourceLocation At):Value(At){public override ScalarType Type=>Storage.Type;}
    private sealed partial class Verifier
    {
        private ReferenceOld? referenceOld;
        private int nextReferenceCapture;
        private Value VerifyReferenceRead(ScalarExpression e)
        {
            if(referenceOld is not { } old||e.Type!=old.Type||e.Left is not null||e.Right is not null||e.Arguments is not null)throw new Rejected(e.Location,"A reference read requires its enclosing verified update.");
            return old;
        }
        private Value VerifyReferenceMutation(ScalarExpression e)
        {
            if(e.Left is null||e.Right is null||e.Arguments is not null||e.Operation=="reference-update"&&e.Value is not ("pre" or "post")||e.Operation=="reference-store"&&e.Value is not null)throw new Rejected(e.Location,"Malformed reference mutation.");
            var storage=Require(VerifyReferenceStorage(e.Left,true,e.Operation!="reference-store",e.Left.Operation == "field"),e.Type,e.Location);
            var saved=referenceOld;var capture=e.Operation=="reference-update"?nextReferenceCapture++:-1;
            if(capture>=0)referenceOld=new(capture,e.Type,e.Location);
            Value value;
            try{value=Require(VerifyValue(e.Right,e.Location),e.Type,e.Location);}finally{referenceOld=saved;}
            if(e.Left.Operation=="local")AssignReference(e.Left);
            return new ReferenceMutation(storage,value,capture,e.Value=="post",e.Location);
        }
        private static bool IndirectReferenceLocation(ScalarExpression e)=>e.Operation is "call" or "ref-select" or "ref-reassign" or "array-index"
            || e is {Operation:"field",Left:not null}&&!IsClassType(e.Left.Type)&&IndirectReferenceLocation(e.Left);
    }
    private sealed partial class Emitter
    {
        private readonly Dictionary<int,string> referenceOldNames=[];
        private string ReferenceMutationAtom(ReferenceMutation mutation)
        {
            var address=StorageAddress(mutation.Storage);var pointer=Temp();
            c.Append(ReferencePointer(mutation.Type,ScalarParameterMode.Ref)).Append(' ').Append(pointer).Append(" = ").Append(address).AppendLine(";");
            string? old=null;
            if(mutation.Capture>=0){old=Temp();c.Append(CType(mutation.Type)).Append(' ').Append(old).Append(" = *").Append(pointer).AppendLine(";");referenceOldNames.Add(mutation.Capture,old);}
            string value;
            try{value=Atom(mutation.Value);}finally{if(mutation.Capture>=0)referenceOldNames.Remove(mutation.Capture);}
            c.Append('*').Append(pointer).Append(" = ").Append(value).AppendLine(";");return mutation.Postfix?old!:value;
        }
    }
}
