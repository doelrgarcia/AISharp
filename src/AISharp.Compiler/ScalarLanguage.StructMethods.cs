namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    /// <summary>Instance value methods borrow writable/readonly storage; deferred bodies own receiver snapshots.</summary>
    public static ScalarParameterMode StructMethodReceiverMode(FunctionDeclaration declaration, bool isReadonly)
        => declaration.IsAsync || ExtendedBinder.DeclarationHasYield(declaration) ? ScalarParameterMode.Value
            : isReadonly ? ScalarParameterMode.In : ScalarParameterMode.Ref;
}
