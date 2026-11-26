namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private IReadOnlyList<int>? BindNamedArguments(List<ScalarExpression> arguments,FunctionDeclaration declaration,SourceLocation at)
        {
            if(!arguments.Any(a=>a.Operation=="named-argument"))return null;
            if(arguments.Count!=declaration.Parameters.Count)Fail("AI3116","The argument count does not match the named call signature.",at);
            var names=activeCompilation?.PartialParameterResolver?.Invoke(declaration.Location.Offset)??declaration.Parameters.Select(p=>IdentifierName(p.Name)).ToArray();
            var ordered=new ScalarExpression[arguments.Count];var sourceOrder=new List<int>();var displaced=false;
            for(var i=0;i<arguments.Count;i++)
            {
                var argument=arguments[i];var target=i;
                if(argument.Operation=="named-argument")
                {
                    target=names.ToList().FindIndex(n=>n==argument.Value);
                    if(target<0)Fail("AI3116","The defining signature has no parameter named '"+argument.Value+"'.",argument.Location);
                    displaced|=target!=i;argument=argument.Left!;
                }
                else if(displaced)Fail("AI3116","A positional argument cannot follow an out-of-position named argument.",argument.Location);
                if(ordered[target] is not null)Fail("AI3116","A parameter cannot receive more than one argument.",argument.Location);
                ordered[target]=argument;sourceOrder.Add(target);
            }
            if(ordered.Any(a=>a is null))Fail("AI3116","Every parameter must receive an argument.",at);
            arguments.Clear();arguments.AddRange(ordered);
            return sourceOrder.Select((target,i)=>target==i).All(s=>s)?null:sourceOrder;
        }
        // A lifted forwarding function keeps argument evaluation in source order without changing the call ABI.
        private ScalarExpression NamedCall(ScalarExpression call,IReadOnlyList<int>? order)
        {
            if(order is null)return call;
            if(call is not{Operation:"call",Arguments:{} arguments})Fail("AI3100","This named call form requires a forwarding call.",call.Location);
            var values=call.Arguments!;var receiver=values.Count-order.Count;
            if(receiver is <0 or >1)Fail("AI3106","Invalid named call receiver.",call.Location);
            var indices=Enumerable.Range(0,receiver).Concat(order.Select(i=>i+receiver)).ToArray();
            var parameters=new List<ScalarParameter>();var forwards=new ScalarExpression[values.Count];
            for(var i=0;i<indices.Length;i++)
            {
                var original=indices[i];var argument=values[original];
                var mode=argument.Operation switch{"argument-ref"=>ScalarParameterMode.Ref,"argument-out"=>ScalarParameterMode.Out,"argument-in" or "argument-temporary" or "argument-receiver-copy"=>ScalarParameterMode.In,_=>ScalarParameterMode.Value};
                var parameter=new ScalarParameter("__named_parameter_"+i,argument.Type){Mode=mode,Scoped=call.ReferenceExcludedArguments?.Contains(original)==true};
                parameters.Add(parameter);forwards[original]=ForwardParameter(parameter,call.Location);
            }
            var inner=call with{Arguments=forwards};var name="__unit_named_call_"+call.Location.Offset+"_"+liftedFunctions!.Count;
            var body=call.Type==ScalarType.Unit?new ScalarStatement[]{new("expression",call.Location,inner),new("return",call.Location)}:[Returning(inner,call.ReferenceMode,call.Location)];
            Lift(new ScalarFunction(name,parameters,call.Type,body,[TrueContract(call.Location)],call.Location){ReturnMode=call.ReferenceMode});
            return call with{Value=name,Arguments=indices.Select(i=>values[i]).ToArray(),ReferenceExcludedArguments=call.ReferenceExcludedArguments is null?null:indices.Select((original,i)=>(original,i)).Where(p=>call.ReferenceExcludedArguments.Contains(p.original)).Select(p=>p.i).ToArray()};
        }
    }
}
