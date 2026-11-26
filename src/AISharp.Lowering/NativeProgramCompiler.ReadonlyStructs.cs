using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static void ValidateReadonlyStructLayouts()
    {
        foreach(var type in layouts??[])
        {
            if(!type.IsReadonly)continue;
            if(type.IsClass||type.IsInterface||type.IsStatic)throw new Rejected(type.Location,"Readonly type metadata requires a value struct.");
            if(type.Fields.Any(f=>!f.IsReadonly||f.IsEvent))throw new Rejected(type.Location,"A readonly struct cannot contain mutable instance storage or a field-like event.");
            foreach(var method in type.Methods)
            {
                if(method.IsConstructor){if(method.IsReadonly)throw new Rejected(method.Declaration.Location,"A constructor cannot have readonly member metadata.");continue;}
                if(method.IsInitAccessor)
                {
                    if(method.IsStatic||method.IsReadonly||method.ReceiverMode!=ScalarParameterMode.Ref)throw new Rejected(method.Declaration.Location,"An init accessor requires a mutable instance receiver during initialization.");
                    continue;
                }
                if(method.IsStatic)continue;
                var expected=ScalarLanguage.ReadonlyStructReceiverMode(method.Declaration);
                if(!method.IsReadonly||method.ReceiverMode!=expected)throw new Rejected(method.Declaration.Location,"A readonly struct member requires a readonly input receiver, or a copied receiver for deferred execution.");
            }
        }
    }
}
