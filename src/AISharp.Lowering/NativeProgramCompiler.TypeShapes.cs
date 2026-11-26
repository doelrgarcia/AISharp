using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record VerifiedTypeShape(ScalarRuntimeTypeKind Kind,ScalarType? NamedType,IReadOnlyList<VerifiedTypeShape> Children,int Rank,string Key,string Display,bool NonNullableValue,bool ElementType);
    private sealed record TypeShapeLiteral(ScalarType Result,VerifiedTypeShape Shape,SourceLocation At):Value(At){public override ScalarType Type=>Result;}
    private sealed partial class Verifier
    {
        private Value VerifyTypeShapeLiteral(ScalarExpression expression)
        {
            if(expression is not {Left:null,Right:null,Value:null,Arguments:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null,RuntimeTypeShape:not null})
                throw new Rejected(expression.Location,"A structural type literal requires only a verified logical type description and native Type result.");
            RequireNativeTypeDescriptor(expression.Type,expression.Location);
            if(expression.RuntimeTypeProof is {} proof&&!ScalarRuntimeTypeConstraints.CheckProof(expression.RuntimeTypeShape,proof,layouts!,typeEnums??[],out var failure))throw new Rejected(expression.Location,failure);
            var visiting=new HashSet<ScalarRuntimeTypeShape>(ReferenceEqualityComparer.Instance);var nodes=0;
            VerifiedTypeShape Shape(ScalarRuntimeTypeShape shape,int depth)
            {
                if(shape is null)throw new Rejected(expression.Location,"A runtime type description cannot contain a null node.");
                if(depth>=64||++nodes>4096||!visiting.Add(shape))throw new Rejected(expression.Location,"A runtime type description is cyclic or exceeds its depth/node limit.");
                try
                {
                    VerifiedTypeShape Named(ScalarType type)
                    {
                        if(!KnownRuntimeType(type))throw new Rejected(expression.Location,"A structural type leaf must name a known bound runtime type.");
                        var nominal=IsStructType(type)?Layout(type):null;
                        var value=type!=ScalarType.Unit&&type!=ScalarType.String&&(Enum.IsDefined(type)||typeEnums?.Any(e=>e.Id==type)==true||nominal is {IsClass:false});
                        var enumeration=typeEnums?.FirstOrDefault(e=>e.Id==type);
                        var element=type!=ScalarType.Unit&&nominal is not {IsStatic:true}&&nominal is not {IsCheckInstance:true,TypeParameters.Count:>0}&&enumeration is not {IsCheckInstance:true,TypeParameters.Count:>0};
                        var display=Emitter.NativeTypeDisplay(type);if(display.Length>65536)throw new Rejected(expression.Location,"A runtime type name exceeds its UTF-16 length limit.");
                        return new(ScalarRuntimeTypeKind.Named,type,[],0,"n:"+((int)type).ToString(CultureInfo.InvariantCulture),display,value&&element,element);
                    }
                    if(shape.Kind==ScalarRuntimeTypeKind.Named)
                    {
                        if(shape is not {NamedType:not null,Element:null,Rank:0,Arguments:null})throw new Rejected(expression.Location,"A named type leaf cannot carry structural children or rank metadata.");
                        return Named(shape.NamedType.Value);
                    }
                    if(shape.Kind==ScalarRuntimeTypeKind.NullableDefinition)
                    {
                        if(shape is not {NamedType:null,Element:null,Rank:0,Arguments:null})throw new Rejected(expression.Location,"An unbound Nullable definition cannot carry children, rank or nominal metadata.");
                        return new(shape.Kind,null,[],0,"qd","System.Nullable`1[T]",false,false);
                    }
                    if(shape.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition)
                    {
                        if(shape is not {NamedType:null,Element:null,Arguments:null}||shape.Rank is <0 or >8)throw new Rejected(expression.Location,"A ValueTuple definition has invalid arity or structural metadata.");
                        var parameters=Enumerable.Range(1,shape.Rank).Select(i=>i==8?"TRest":"T"+i.ToString(CultureInfo.InvariantCulture));
                        var display="System.ValueTuple"+(shape.Rank==0?"":"`"+shape.Rank.ToString(CultureInfo.InvariantCulture)+"["+string.Join(",",parameters)+"]");
                        return new(shape.Kind,null,[],shape.Rank,"td"+shape.Rank.ToString(CultureInfo.InvariantCulture),display,shape.Rank==0,shape.Rank==0);
                    }
                    if(shape.Kind==ScalarRuntimeTypeKind.ValueTuple)
                    {
                        if(shape is not {NamedType:null,Element:null,Rank:0,Arguments:not null}||shape.Arguments.Count is <1 or >8)throw new Rejected(expression.Location,"A ValueTuple type has invalid structural metadata or arity.");
                        var arguments=shape.Arguments.Select(a=>Shape(a,depth+1)).ToArray();
                        if(arguments.Any(a=>!a.ElementType))throw new Rejected(expression.Location,"A ValueTuple argument cannot be void, static or an unbound generic definition.");
                        if(arguments.Length==8&&!arguments[7].NonNullableValue)throw new Rejected(expression.Location,"The eighth ValueTuple argument must be a non-nullable value type.");
                        if(expression.RuntimeTypeProof is null&&!ScalarRuntimeTypeConstraints.CheckValueTuple(shape.Arguments,layouts!,typeEnums??[],out _,out var tupleFailure))throw new Rejected(expression.Location,tupleFailure);
                        var prefix="System.ValueTuple`"+arguments.Length.ToString(CultureInfo.InvariantCulture)+"[";
                        if(prefix.Length+arguments.Sum(a=>(long)a.Display.Length)+arguments.Length>65536)throw new Rejected(expression.Location,"A runtime type name exceeds its UTF-16 length limit.");
                        return new(shape.Kind,null,arguments,0,"t:"+string.Join(";",arguments.Select(a=>a.Key.Length.ToString(CultureInfo.InvariantCulture)+":"+a.Key)),prefix+string.Join(",",arguments.Select(a=>a.Display))+"]",true,true);
                    }
                    if(shape.Kind is ScalarRuntimeTypeKind.Array or ScalarRuntimeTypeKind.Nullable)
                    {
                        if(shape is not {NamedType:null,Element:not null,Arguments:null}||shape.Kind==ScalarRuntimeTypeKind.Array&&shape.Rank is <1 or >32||shape.Kind==ScalarRuntimeTypeKind.Nullable&&shape.Rank!=0)
                            throw new Rejected(expression.Location,"An array or nullable type has invalid element/rank metadata.");
                        var element=Shape(shape.Element,depth+1);
                        if(shape.Kind==ScalarRuntimeTypeKind.Nullable)
                        {
                            if(!element.NonNullableValue)throw new Rejected(expression.Location,"A nullable type requires a non-nullable value type.");
                            if((long)element.Display.Length+"System.Nullable`1[".Length+1>65536)throw new Rejected(expression.Location,"A runtime type name exceeds its UTF-16 length limit.");
                            return new(shape.Kind,null,[element],0,"q:"+element.Key,"System.Nullable`1["+element.Display+"]",false,true);
                        }
                        if(!element.ElementType)throw new Rejected(expression.Location,"An array element cannot be void, static or an unbound generic definition.");
                        if(element.Display.Length+shape.Rank+1>65536)throw new Rejected(expression.Location,"A runtime type name exceeds its UTF-16 length limit.");
                        if(shape.Rank==1&&element.NamedType==ScalarType.String&&layouts!.FirstOrDefault(s=>s.IsStringArray) is {} array)return Named(array.Id);
                        if(shape.Rank==1&&element.NamedType is { } named&&layouts!.FirstOrDefault(s=>s.ArrayElementType==named) is {} typedArray)return Named(typedArray.Id);
                        return new(shape.Kind,null,[element],shape.Rank,"a:"+shape.Rank.ToString(CultureInfo.InvariantCulture)+":"+element.Key,element.Display+"["+new string(',',shape.Rank-1)+"]",false,true);
                    }
                    if(shape.Kind==ScalarRuntimeTypeKind.Constructed)
                    {
                        if(shape is not {NamedType:not null,Element:null,Rank:0,Arguments:not null}||!IsStructType(shape.NamedType.Value))throw new Rejected(expression.Location,"A constructed logical type requires a bound generic definition and argument descriptions.");
                        var definition=Layout(shape.NamedType.Value);var definitionIndex=(int)definition.Id-ScalarLanguage.StructTypeBase;
                        if(!definition.IsCheckInstance||definition.Template!=definitionIndex||definition.TypeParameters.Count==0||definition.TypeParameters.Count!=shape.Arguments.Count||definition.IsStatic)
                            throw new Rejected(expression.Location,"The logical generic definition, arity or constructible type category is invalid.");
                        var arguments=shape.Arguments.Select(a=>Shape(a,depth+1)).ToArray();if(arguments.Any(a=>!a.ElementType))throw new Rejected(expression.Location,"A generic argument cannot be void, static or an unbound generic definition.");
                        if(arguments.All(a=>a.Kind==ScalarRuntimeTypeKind.Named))
                        {
                            var bound=layouts!.FirstOrDefault(s=>!s.IsCheckInstance&&s.Template==definitionIndex&&s.TypeArgumentList.SequenceEqual(arguments.Select(a=>a.NamedType!.Value)));
                            if(bound is null)throw new Rejected(expression.Location,"A storage-representable generic type must use its bound nominal runtime identity.");
                            return Named(bound.Id);
                        }
                        if(definition.RuntimeTypeDefinition is not {} metadata||metadata.Arity!=arguments.Length)throw new Rejected(expression.Location,"Generic logical declaration metadata is incomplete.");
                        if(expression.RuntimeTypeProof is null&&!ScalarRuntimeTypeConstraints.Check(definition.Id,shape.Arguments,layouts!,typeEnums??[],out _,out var constraintFailure))throw new Rejected(expression.Location,constraintFailure);
                        var prefix=definition.SourceRuntimeFullName is not null||definition.SourceFullName is not null?"":definition.SourceName is not null?definition.SourceNamespace+".":string.IsNullOrEmpty(typeModule)?"":typeModule+".";
                        var definitionName=definition.SourceRuntimeFullName??definition.SourceFullName??definition.SourceName??definition.Name.Split('<')[0];
                        if((long)prefix.Length+definitionName.Length+3+arguments.Length.ToString(CultureInfo.InvariantCulture).Length+arguments.Sum(a=>(long)a.Display.Length)+Math.Max(0,arguments.Length-1)>65536)throw new Rejected(expression.Location,"A runtime type name exceeds its UTF-16 length limit.");
                        return new(shape.Kind,definition.Id,arguments,0,"g:"+((int)definition.Id).ToString(CultureInfo.InvariantCulture)+":"+string.Join(";",arguments.Select(a=>a.Key.Length.ToString(CultureInfo.InvariantCulture)+":"+a.Key)),prefix+definitionName+((definition.SourceOwnArity??arguments.Length) is var own&&own>0?"`"+own.ToString(CultureInfo.InvariantCulture):"")+"["+string.Join(",",arguments.Select(a=>a.Display))+"]",!metadata.IsClass,!metadata.IsStatic);
                    }
                    throw new Rejected(expression.Location,"Unknown runtime type description kind.");
                }
                finally{visiting.Remove(shape);}
            }
            return new TypeShapeLiteral(expression.Type,Shape(expression.RuntimeTypeShape,0),expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private readonly Dictionary<string,(VerifiedTypeShape Shape,int Id)> shapeLiterals=new(StringComparer.Ordinal);
        private int shapeNameUnits;
        private string EmitTypeShapeLiteral(TypeShapeLiteral literal)
        {
            if(literal.Shape.Kind==ScalarRuntimeTypeKind.Named)return EmitTypeLiteral(new(literal.Type,literal.Shape.NamedType!.Value,literal.Location));
            if(!shapeLiterals.TryGetValue(literal.Shape.Key,out var entry))
            {
                if(shapeLiterals.Count>=4096)throw new Rejected(literal.Location,"Too many runtime type descriptions.");
                if((long)shapeNameUnits+literal.Shape.Display.Length>1048576)throw new Rejected(literal.Location,"Runtime type names exceed the aggregate UTF-16 limit.");
                shapeNameUnits+=literal.Shape.Display.Length;
                var first=Math.Max((long)ScalarLanguage.ObjectTypeId+1,Math.Max((long)ScalarLanguage.StructTypeBase+layouts!.Count,(long)ScalarLanguage.EnumTypeBase+(typeEnums?.Count??0)));
                if(first+shapeLiterals.Count>int.MaxValue)throw new Rejected(literal.Location,"Runtime type identities exceed the supported domain.");
                entry=(literal.Shape,(int)(first+shapeLiterals.Count));shapeLiterals.Add(literal.Shape.Key,entry);
            }
            typeLiterals.Add(entry.Id);return "("+CType(literal.Type)+")&"+TypeLiteralName(entry.Id);
        }
    }
}
