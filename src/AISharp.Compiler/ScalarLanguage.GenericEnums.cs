namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static ScalarEnum SaveEnumRuntimeDefinition(ScalarEnum enumeration)
    {
        if(enumeration.GenericOwner is not {} ownerId||activeStructs is null)return enumeration;
        var index=(int)ownerId-StructTypeBase;if(index<0||index>=activeStructs.Count)return enumeration;
        var parent=SaveRuntimeTypeDefinition(activeStructs[index],true);
        if(parent.RuntimeTypeDefinition is not {} proof||proof.Parameters.Count!=enumeration.TypeParameters.Count)return enumeration;
        return enumeration with{RuntimeTypeDefinition=new(false,false,false,enumeration.TypeParameters.Count,proof.HasConstraints)
        {
            HasDefaultConstructor=true,HasPhysicalDefaultConstructor=true,
            Parameters=proof.Parameters.Select((p,i)=>p with{Name=enumeration.TypeParameters[i]}).ToArray()
        }};
    }
    private static bool EnumDefinitionType(ScalarEnum enumeration)=>enumeration.TypeParameters.Count>0&&enumeration.Template==(int)enumeration.Id-EnumTypeBase;
    private static bool OpenEnumShape(ScalarRuntimeTypeShape shape,int depth=0)
    {
        if(depth>=64)return true;
        if(shape.Kind==ScalarRuntimeTypeKind.Parameter)return true;
        if(shape.NamedType is {} id)
        {
            var index=(int)id-StructTypeBase;
            if(index>=0&&activeStructs is not null&&index<activeStructs.Count&&activeStructs[index] is {IsTypeParameter:true} or {IsCheckInstance:true})return true;
            if(activeEnums?.FirstOrDefault(e=>e.Id==id) is {IsCheckInstance:true})return true;
        }
        return shape.Element is not null&&OpenEnumShape(shape.Element,depth+1)||shape.Arguments?.Any(a=>OpenEnumShape(a,depth+1))==true;
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarEnum? EnumDefinition(string name)=>_enums.FirstOrDefault(e=>e.Name==IdentifierName(name)&&EnumDefinitionType(e));
        private string EnumShapeText(ScalarRuntimeTypeShape shape)=>shape.Kind switch
        {
            ScalarRuntimeTypeKind.Named=>HierarchyTypeName(shape.NamedType!.Value),
            ScalarRuntimeTypeKind.Array=>EnumShapeText(shape.Element!)+"["+new string(',',shape.Rank-1)+"]",
            ScalarRuntimeTypeKind.Nullable=>"System.Nullable<"+EnumShapeText(shape.Element!)+">",
            ScalarRuntimeTypeKind.Constructed=>TypeIdentifierSyntax(_enums.FirstOrDefault(e=>e.Id==shape.NamedType!.Value)?.Name??_structs[IndexOf(shape.NamedType!.Value)].Name)+"<"+string.Join(",",shape.Arguments!.Select(EnumShapeText))+">",
            ScalarRuntimeTypeKind.ValueTuple=>"System.ValueTuple<"+string.Join(",",shape.Arguments!.Select(EnumShapeText))+">",
            ScalarRuntimeTypeKind.ValueTupleDefinition when shape.Rank==0=>"System.ValueTuple",
            _=>throw Error("AI3106","An enum argument must be a bound logical type.",shape.Location)
        };
        private ScalarType? GenericEnumType(string text,SourceLocation at)
        {
            var open=text.IndexOf('<');if(open<0||!text.EndsWith('>')||EnumDefinition(text[..open]) is not {} definition)return null;
            var tokens=_tokens;var position=_position;var annotations=activeRuntimeQueryAnnotations;
            try
            {
                _tokens=Lex(text[(open+1)..^1],at,Has(ScalarFeatures.Floats)||Has(ScalarFeatures.Decimals),Has(ScalarFeatures.Chars),Has(ScalarFeatures.Strings));_position=0;activeRuntimeQueryAnnotations??=[];
                var arguments=new List<ScalarRuntimeTypeShape>();
                do{arguments.Add(ReadRuntimeTypeShape(1));}while(Take(","));
                if(Current.Text!="")Fail("AI3106","Invalid enum type arguments.",at);
                return EnumInstance(definition,arguments,at);
            }
            finally{_tokens=tokens;_position=position;activeRuntimeQueryAnnotations=annotations;}
        }
        private ScalarType EnumInstance(ScalarEnum definition,IReadOnlyList<ScalarRuntimeTypeShape> arguments,SourceLocation at)
        {
            if(arguments.Count!=definition.TypeParameters.Count)Fail("AI3116","The enum type does not match its containing generic arity.",at);
            foreach(var argument in arguments)if(!IsRuntimeElementType(argument))Fail("AI3106","An enum argument must be a bound non-void type.",argument.Location);
            var views=_structs.Select(SaveRuntimeTypeDefinition).ToArray();
            var parent=definition.GenericOwner??throw Error("AI3107","The enum has no containing generic declaration.",at);
            if(!ScalarRuntimeTypeConstraints.Check(parent,arguments,views,_enums,out _,out var reason))Fail("AI3107",reason,at);
            string Key(ScalarRuntimeTypeShape shape)=>ScalarRuntimeTypeConstraints.IdentityKey(shape,views,_enums);
            var keys=arguments.Select(Key).ToArray();
            var known=_enums.FirstOrDefault(e=>e.Template==definition.Template&&!EnumDefinitionType(e)&&e.TypeArguments.Count==arguments.Count&&e.TypeArguments.Select(Key).SequenceEqual(keys));
            if(known is not null)return known.Id;
            if(_enums.Count>=1000)Fail("AI3100","More than 1000 enum identities are unsupported.",at);
            var value=definition with{Name=definition.Name+"<"+string.Join(",",arguments.Select(EnumShapeText))+">",Id=(ScalarType)(EnumTypeBase+_enums.Count),TypeArguments=Array.AsReadOnly(arguments.ToArray()),IsCheckInstance=arguments.Any(a=>OpenEnumShape(a))};
            _enums.Add(value);return value.Id;
        }
    }
}
