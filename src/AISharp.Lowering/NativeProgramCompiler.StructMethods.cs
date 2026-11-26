using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static void ValidateStructMethodLayouts()
    {
        foreach(var type in layouts??[])
        {
            foreach(var method in type.Methods)
            {
                if(method.IsStatic||method.IsConstructor)
                {
                    if(method.ReceiverMode!=ScalarParameterMode.Value)
                        throw new Rejected(method.Declaration.Location,"A static method or constructor cannot declare an implicit borrowed receiver.");
                    continue;
                }
                if(type.IsClass||type.IsInterface||type.IsTypeParameter||method.IsAccessor
                    ||method.Declaration.Body.ScalarSource is not {Text.Length:>0})continue;
                var expected=ScalarLanguage.StructMethodReceiverMode(method.Declaration,method.IsReadonly);
                if(method.ReceiverMode!=expected)
                    throw new Rejected(method.Declaration.Location,"A struct method must preserve its writable, readonly or deferred receiver ABI.");
            }
        }
    }
}
