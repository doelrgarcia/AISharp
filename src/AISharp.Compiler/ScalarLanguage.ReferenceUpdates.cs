namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private bool ReferenceVariable(ScalarExpression e) => e.Operation is "ref-select" or "ref-reassign"
            || e.Operation == "call" && e.ReferenceMode != ScalarParameterMode.Value
            || e.Operation == "local" && _referenceParameters.ContainsKey(e.Value!)
            || e is {Operation:"field",Left:not null} && !IsClass(e.Left.Type) && ReferenceVariable(e.Left);
        private bool ReferenceAlias(ScalarExpression e) => e.Operation is "ref-select" or "ref-reassign"
            || e.Operation == "call" && e.ReferenceMode != ScalarParameterMode.Value
            || e.Operation == "local" && _referenceLocals.Contains(e.Value!)
            || e is {Operation:"field",Left:not null} && !IsClass(e.Left.Type) && ReferenceAlias(e.Left);
        private static bool ReassignsReference(ScalarExpression e) => e.Operation == "ref-reassign"
            || e.Left is not null && ReassignsReference(e.Left) || e.Right is not null && ReassignsReference(e.Right) || e.Arguments?.Any(ReassignsReference)==true;
        private ScalarExpression ReferenceWrite(ScalarExpression storage,ScalarExpression value,SourceLocation at)
        {
            CheckReferenceAwaitTarget(storage,value,at);
            return new("reference-store",storage.Type,at,Left:storage,Right:Coerce(value,storage.Type));
        }
        private ScalarExpression ReferenceUpdate(ScalarExpression storage,string op,ScalarExpression value,bool postfix,SourceLocation at)
        {
            CheckReferenceAwaitTarget(storage,value,at);
            var old=new ScalarExpression("reference-read",storage.Type,at);
            var result=CompoundValue(old,op,value,at);
            return new("reference-update",storage.Type,at,postfix?"post":"pre",Left:storage,Right:result);
        }
        private void CheckReferenceAwaitTarget(ScalarExpression storage,ScalarExpression value,SourceLocation at)
        {
            var root=RootOf(storage);
            if(HasAwaitExpr(value)&&(root.Operation is "ref-select" or "ref-reassign"||root.Operation=="call"&&root.ReferenceMode!=ScalarParameterMode.Value))
                Fail("AI3106","A reference result cannot be retained as an assignment target across await.",at);
        }
    }
}
