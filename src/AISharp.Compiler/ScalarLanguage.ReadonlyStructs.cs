namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    /// <summary>Deferred readonly execution owns a receiver copy; synchronous calls borrow an input receiver.</summary>
    public static ScalarParameterMode ReadonlyStructReceiverMode(FunctionDeclaration declaration)
        => declaration.IsAsync || ExtendedBinder.DeclarationHasYield(declaration) ? ScalarParameterMode.Value : ScalarParameterMode.In;

    private static void EnsureReadonlyStructMembers()
    {
        foreach(var owner in activeStructs!.ToArray())
        {
            if(!owner.IsReadonly)continue;
            if(owner.IsClass||owner.IsInterface||owner.IsStatic)throw DeclarationError("AI3106","Readonly type declarations require a value struct.",owner.Location);
            var mutable=owner.Fields.FirstOrDefault(f=>!f.IsReadonly);
            if(mutable is not null)throw DeclarationError("AI3106","Instance storage of a readonly struct must be readonly.",mutable.Location);
            // Deferred execution owns a value snapshot; it must not retain a caller's
            // input reference across suspension.
            activeStructs[IndexOf(owner.Id)]=owner with{Methods=owner.Methods.Select(m=>m.IsStatic||m.IsConstructor||m.IsInitAccessor?m:m with{IsReadonly=true,ReceiverMode=ReadonlyStructReceiverMode(m.Declaration)}).ToArray()};
        }
    }
}
