namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private ScalarExpression DelayedBoxedAccessorStore(ScalarExpression call,ScalarExpression receiver,ScalarProperty property,string setter,SourceLocation at)
        {
            if(!Has(ScalarFeatures.Boxing)||property.IsStatic||!IsStruct(receiver.Type)||IsClass(receiver.Type)||StructOf(receiver.Type).IsTypeParameter||!_structs[property.Owner].IsInterface)return call;
            var root=_structs[property.Owner].Methods.First(m=>m.QualifiedName==setter);var implementation=ResolveInterfaceImplementation(IndexOf(receiver.Type),root).Method;
            if(implementation is null||!_structs[implementation.Owner].IsInterface)return call;
            var box=ValueBox(receiver.Type,at);var capture=IsReferenceStorage(receiver)&&WritableAccessorReceiver(receiver)
                ?new ScalarExpression("argument-in",receiver.Type,at,"forwarded",Left:receiver):new ScalarExpression("argument-temporary",receiver.Type,at,Left:receiver);
            var arguments=call.Arguments!;return call with{Arguments=[new("accessor-box",arguments[0].Type,at,((int)box.Id).ToString(System.Globalization.CultureInfo.InvariantCulture),Left:capture),..arguments.Skip(1)]};
        }
        private bool NeedsBoxedAccessorBridge(ScalarType receiver,ScalarProperty property)
        {
            var owner=_structs[property.Owner];
            foreach(var name in new[]{property.Getter,property.Setter}.OfType<string>())
            {
                var root=owner.Methods.First(m=>m.QualifiedName==name);
                if(ResolveInterfaceImplementation(IndexOf(receiver),root).Method is {} implementation&&_structs[implementation.Owner].IsInterface)return true;
            }
            return false;
        }
        private ScalarExpression BoxedAccessorCall(ScalarExpression receiver,ScalarProperty property,ScalarStructMethod root,ScalarStructMethod implementation,IReadOnlyList<ScalarExpression> arguments,ScalarType result,SourceLocation at)
        {
            var owner=StructOf(receiver.Type);var name="$boxed_accessor<"+root.QualifiedName+">";
            var wrapper=owner.Methods.FirstOrDefault(m=>m.IsLifted&&m.Name==name);
            if(wrapper is null)
            {
                if(_structs.SelectMany(t=>t.Methods).Count(m=>m.IsLifted&&m.Name.StartsWith("$boxed_accessor<",StringComparison.Ordinal))>=256)
                    Fail("AI3100","More than 256 boxed interface accessor bindings are unsupported.",at);
                var declaring=_structs[root.Owner];var parameterTypes=root.Declaration.Parameters.Select(p=>MemberType(declaring.TypeArguments,p.Type,at)).ToArray();
                string ClosedParameter(ParameterDeclaration p,int index)
                    =>(ScopedReference(p.Type)?"scoped ":"")+PassingPrefix(ParameterPassing(p.Type).Mode)+SignatureTypeName(parameterTypes[index]);
                var declaration=new FunctionDeclaration(name,root.Declaration.Parameters.Select((p,i)=>new ParameterDeclaration("__argument_"+i,ClosedParameter(p,i))).ToArray(),PassingPrefix(ParameterPassing(root.Declaration.ReturnType).Mode)+SignatureTypeName(result),[],[],new FunctionBody([]),[],at);
                var readonlyReceiver=owner.IsReadonly&&!root.IsInitAccessor;var mode=readonlyReceiver?ScalarParameterMode.In:ScalarParameterMode.Ref;
                wrapper=new(name,owner.Name+"."+name,false,false,declaration){Owner=IndexOf(owner.Id),IsLifted=true,IsAccessor=true,AccessorMember=root.AccessorMember,IsInitAccessor=root.IsInitAccessor,IsReadonly=readonlyReceiver,ReceiverMode=mode};
                _structs[IndexOf(owner.Id)]=owner with{Methods=[..owner.Methods,wrapper]};
                var self=new ScalarParameter("__receiver",owner.Id){Mode=mode,Scoped=true};
                var parameters=root.Declaration.Parameters.Select((p,i)=>new ScalarParameter("__argument_"+i,parameterTypes[i]){Mode=ParameterPassing(p.Type).Mode,Scoped=ScopedReference(p.Type)}).ToList();
                var read=new ScalarExpression("local",owner.Id,at,self.Name);var values=parameters.Select(p=>ForwardParameter(p,at)).ToList();ScalarExpression call;
                var targetOwner=_structs[implementation.Owner];
                if(targetOwner.IsInterface)call=InterfaceImplementationCall(implementation,BoxValue(read,targetOwner.Id),values,result,at);
                else
                {
                    var target=implementation.ReceiverMode==ScalarParameterMode.Value?read:ForwardArgument(read,implementation.ReceiverMode);
                    call=AccessorReferenceResult(ReferenceResult(new("call",result,at,implementation.QualifiedName){Arguments=[target,..values]},implementation.Declaration.ReturnType,implementation.Declaration.Parameters,1),implementation);
                }
                var returned=ParameterPassing(root.Declaration.ReturnType).Mode;
                Lift(new ScalarFunction(wrapper.QualifiedName,[self,..parameters],result,result==ScalarType.Unit?[new("expression",at,call)]:[Returning(call,returned,at)],[TrueContract(at)],at){ReturnMode=returned});
            }
            var captured=AccessorReceiverArgument(wrapper,receiver,at);
            if(wrapper.ReceiverMode==ScalarParameterMode.In&&IsReferenceStorage(receiver)&&!WritableAccessorReceiver(receiver))captured=new("argument-receiver-copy",receiver.Type,at,"readonly",Left:receiver);
            return AccessorReferenceResult(ReferenceResult(new("call",result,at,wrapper.QualifiedName){Arguments=[captured,..arguments]},wrapper.Declaration.ReturnType,wrapper.Declaration.Parameters,1),wrapper);
        }
        private static string PassingPrefix(ScalarParameterMode mode)=>mode switch
        {ScalarParameterMode.Ref=>"ref ",ScalarParameterMode.Out=>"out ",ScalarParameterMode.In=>"in ",ScalarParameterMode.RefReadonly=>"ref readonly ",_=>""};
    }
}
