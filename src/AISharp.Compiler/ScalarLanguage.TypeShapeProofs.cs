namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record RuntimeQueryAnnotation(bool IsValue,bool AllowsRoot);
    private sealed record RuntimeQueryDeclaration(IReadOnlyList<ScalarType> Symbols,IReadOnlyList<ScalarRuntimeGenericParameter> Parameters,ScalarRuntimeTypeShape Shape,IReadOnlyDictionary<int,RuntimeQueryAnnotation> Annotations);
    [ThreadStatic] private static Dictionary<int,RuntimeQueryDeclaration>? runtimeQueryProofs;
    private sealed partial class ExtendedBinder
    {
        private RuntimeQueryDeclaration? activeRuntimeQueryProof;
        private Dictionary<int,RuntimeQueryAnnotation>? activeRuntimeQueryAnnotations;
        private ScalarRuntimeTypeProof? BindRuntimeTypeProof(ScalarRuntimeTypeShape shape,SourceLocation at)
        {
            var scope=ScopeTypeArguments(_typeArgs??[],_struct,_function is null?null:GenericOf(_function)).OrderBy(p=>(int)p.Key).ToArray();
            if(activeRuntimeQueryProof is null&&_checkOnly&&scope.Length>0)
            {
                var symbols=scope.Select(p=>p.Key).ToArray();var map=symbols.Select((s,i)=>(s,i)).ToDictionary(p=>p.s,p=>p.i);
                foreach(var item in scope)if(IsTypeParameterType(item.Value))map[item.Value]=map[item.Key];
                ScalarRuntimeTypeShape EncodeType(ScalarType type,int depth=0)
                {
                    if(depth>=64)Fail("AI3100","Generic type-query proof exceeds its nesting limit.",at);
                    if(map.TryGetValue(type,out var index))return new(ScalarRuntimeTypeKind.Parameter,at){Rank=index};
                    if(IsEnum(type)&&EnumOf(type) is {Template:>=0,TypeArguments.Count:>0} enumeration)
                    {
                        ScalarRuntimeTypeShape EncodeEnum(ScalarRuntimeTypeShape node)=>node.Kind==ScalarRuntimeTypeKind.Named?EncodeType(node.NamedType!.Value,depth+1):node with{Element=node.Element is null?null:EncodeEnum(node.Element),Arguments=node.Arguments?.Select(EncodeEnum).ToArray()};
                        return new(ScalarRuntimeTypeKind.Constructed,at){NamedType=_enums[enumeration.Template].Id,Arguments=enumeration.TypeArguments.Select(EncodeEnum).ToArray()};
                    }
                    if(IsStruct(type)&&StructOf(type) is {Template:>=0,TypeArgumentList.Count:>0} info)
                        return new(ScalarRuntimeTypeKind.Constructed,at){NamedType=_structs[info.Template].Id,Arguments=info.TypeArgumentList.Select(t=>EncodeType(t,depth+1)).ToArray()};
                    return new(ScalarRuntimeTypeKind.Named,at){NamedType=type};
                }
                ScalarRuntimeTypeShape Encode(ScalarRuntimeTypeShape node)=>node.Kind==ScalarRuntimeTypeKind.Named?EncodeType(node.NamedType!.Value)
                    :node with{Element=node.Element is null?null:Encode(node.Element),Arguments=node.Arguments?.Select(Encode).ToArray()};
                bool UsesParameter(ScalarRuntimeTypeShape node)=>node.Kind==ScalarRuntimeTypeKind.Parameter||node.Element is not null&&UsesParameter(node.Element)||node.Arguments?.Any(UsesParameter)==true;
                var declared=Encode(shape);
                if(UsesParameter(declared))
                {
                    var parameters=symbols.Select(symbol=>
                    {
                        var info=StructOf(symbol);var bases=info.ParameterBounds;var baseIndex=bases?.DeclaredBaseIndex??info.BaseIndex;var interfaces=bases?.DeclaredInterfaces??info.Interfaces;
                        var bounds=interfaces.Select(i=>EncodeType(_structs[i].Id)).ToList();if(baseIndex>=0)bounds.Insert(0,EncodeType(_structs[baseIndex].Id));
                        var dependencies=bases?.Dependencies.Select(d=>map.TryGetValue(d.Type,out var i)?i:throw Error("AI3106","A generic type-query dependency has no parameter identity.",at)).ToArray()??[];
                        return new ScalarRuntimeGenericParameter(info.Name,info.ReferenceKnown,info.HasValueConstraint,info.HasUnmanagedConstraint,info.HasConstructorConstraint,bounds,dependencies);
                    }).ToArray();
                    runtimeQueryProofs!.TryAdd(at.Offset,new(symbols,parameters,declared,new Dictionary<int,RuntimeQueryAnnotation>(activeRuntimeQueryAnnotations!)));
                }
                return null;
            }
            if(activeRuntimeQueryProof is not {} source)return null;
            if(!scope.Select(p=>p.Key).SequenceEqual(source.Symbols))Fail("AI3106","Generic type-query specialization changed its declaration parameter identities.",at);
            var bindings=scope.Select(p=>new ScalarRuntimeTypeShape(ScalarRuntimeTypeKind.Named,at){NamedType=p.Value}).ToArray();
            var proof=new ScalarRuntimeTypeProof(source.Parameters,bindings,source.Shape);var views=_structs.Select(SaveRuntimeTypeDefinition).ToArray();
            if(!ScalarRuntimeTypeConstraints.CheckProof(shape,proof,views,_enums,out var reason,allowOpenBindings:true))Fail("AI3107",reason,at);
            return proof;
        }
    }
}
