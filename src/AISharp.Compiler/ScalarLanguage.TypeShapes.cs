using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static HashSet<ScalarType>? runtimeQueryTypes;
    private static void RememberRuntimeQueryShape(ScalarRuntimeTypeShape shape)
    {
        if(shape.NamedType is {} type&&(int)type>=StructTypeBase)runtimeQueryTypes!.Add(type);
        if(shape.Element is {} element)RememberRuntimeQueryShape(element);
        if(shape.Arguments is {} arguments)foreach(var argument in arguments)RememberRuntimeQueryShape(argument);
    }
    private static int RuntimeQueryRetentionCount()
    {
        if(runtimeQueryTypes is not {Count:>0}&&!activeEnums!.Any(e=>e.GenericOwner is not null))return 0;
        var count=0;var seen=new HashSet<ScalarType>();
        void Shape(ScalarRuntimeTypeShape shape)
        {
            if(shape.NamedType is {} type)Type(type);
            if(shape.Element is {} element)Shape(element);
            if(shape.Arguments is {} arguments)foreach(var argument in arguments)Shape(argument);
        }
        void Type(ScalarType type)
        {
            if(activeEnums!.FirstOrDefault(e=>e.Id==type) is {} enumeration)
            {
                if(!seen.Add(type))return;
                if(enumeration.GenericOwner is {} enumParent)Type(enumParent);
                foreach(var argument in enumeration.TypeArguments)Shape(argument);
                return;
            }
            var index=(int)type-StructTypeBase;
            if(index<0||index>=activeStructs!.Count||!seen.Add(type))return;
            count=Math.Max(count,index+1);var owner=SaveRuntimeTypeDefinition(activeStructs[index]);
            if(owner.Template>=0&&owner.Template!=index&&owner.TypeArgumentList.Count>0){Type(activeStructs[owner.Template].Id);foreach(var argument in owner.TypeArgumentList)Type(argument);}
            if(owner.RuntimeTypeDefinition is not {} definition)return;
            foreach(var parameter in definition.Parameters)foreach(var bound in parameter.Bounds)Shape(bound);
            if(definition.BaseType is {} parent)Shape(parent);
            foreach(var contract in definition.Interfaces)Shape(contract);
            foreach(var field in definition.InstanceFields)Shape(field);
        }
        foreach(var type in runtimeQueryTypes!)Type(type);
        foreach(var enumeration in activeEnums!.Where(e=>e.GenericOwner is not null))Type(enumeration.Id);
        return count;
    }
    private static ScalarStruct SaveRuntimeTypeDefinition(ScalarStruct owner)
        =>SaveRuntimeTypeDefinition(owner,false);
    private static ScalarStruct SaveRuntimeTypeDefinition(ScalarStruct owner,bool force)
    {
        if(!force&&(activeFeatures&ScalarFeatures.TypeShapes)==0&&!activeEnums!.Any(e=>e.GenericOwner is not null))return owner;
        owner=owner with{RuntimeTypeArgumentAllowed=ValidStaticInterfaceArgument(owner.Id)};
        if(owner.TypeParameters.Count==0||owner.Template!=IndexOf(owner.Id)||!typeGenerics!.TryGetValue(IndexOf(owner.Id),out var generic))return owner;
        var all=activeStructs!;var symbols=generic.Parameters.Select((p,i)=>(Symbol:(ScalarType)(StructTypeBase+p.Placeholder),Index:i)).ToDictionary(t=>t.Symbol,t=>t.Index);
        ScalarRuntimeTypeShape Shape(ScalarType type,int depth=0)
        {
            if(depth>=64)throw DeclarationError("AI3100","Generic logical declaration metadata exceeds its nesting limit.",owner.Location);
            if(symbols.TryGetValue(type,out var index))return new(ScalarRuntimeTypeKind.Parameter,owner.Location){Rank=index};
            if(activeEnums!.FirstOrDefault(e=>e.Id==type) is {Template:>=0,TypeArguments.Count:>0} enumeration)
            {
                ScalarRuntimeTypeShape EnumShape(ScalarRuntimeTypeShape shape)=>shape.Kind==ScalarRuntimeTypeKind.Named?Shape(shape.NamedType!.Value,depth+1):shape with{Element=shape.Element is null?null:EnumShape(shape.Element),Arguments=shape.Arguments?.Select(EnumShape).ToArray()};
                return new(ScalarRuntimeTypeKind.Constructed,owner.Location){NamedType=activeEnums![enumeration.Template].Id,Arguments=enumeration.TypeArguments.Select(EnumShape).ToArray()};
            }
            if((int)type>=StructTypeBase&&IndexOf(type)<all.Count&&type!=(ScalarType)ObjectTypeId)
            {
                var info=all[IndexOf(type)];
                if(info.Template>=0&&info.TypeArgumentList.Count>0)
                    return new(ScalarRuntimeTypeKind.Constructed,owner.Location){NamedType=all[info.Template].Id,Arguments=info.TypeArgumentList.Select(t=>Shape(t,depth+1)).ToArray()};
            }
            return new(ScalarRuntimeTypeKind.Named,owner.Location){NamedType=type};
        }
        var parameters=generic.Parameters.Select(p=>
        {
            var proof=all[p.Placeholder];var bases=proof.ParameterBounds;var declaredBase=bases?.DeclaredBaseIndex??proof.BaseIndex;var interfaces=bases?.DeclaredInterfaces??proof.Interfaces;
            var bounds=interfaces.Select(i=>Shape(all[i].Id)).ToList();if(declaredBase>=0)bounds.Insert(0,Shape(all[declaredBase].Id));
            var dependencies=bases?.Dependencies.Select(d=>symbols.TryGetValue(d.Type,out var i)?i:throw DeclarationError("AI3106","A logical parameter dependency has no declaration identity.",d.Location)).ToArray()??[];
            return new ScalarRuntimeGenericParameter(p.Name,proof.ReferenceKnown,proof.HasValueConstraint,proof.HasUnmanagedConstraint,proof.HasConstructorConstraint,bounds,dependencies);
        }).ToArray();
        var physicalCtor=!owner.IsClass||!owner.IsAbstract&&!owner.IsInterface&&!owner.IsStatic&&!owner.IsDelegate&&!owner.IsStringArray
            &&(owner.Methods.Any(m=>m.IsConstructor)?owner.Methods.Any(m=>m.IsConstructor&&m.IsPublic&&!m.IsInternalConstructor&&m.Declaration.Parameters.Count==0):!owner.IsRecord||owner.Positional==0);
        var ctor=physicalCtor&&!owner.Fields.Any(f=>f.IsRequired)&&!owner.Properties.Any(p=>p.IsRequired);
        return owner with{RuntimeTypeDefinition=new(owner.IsClass,owner.IsInterface,owner.IsStatic,owner.TypeParameters.Count,generic.Parameters.Any(p=>p.Constraints.Count>0))
        {
            HasDefaultConstructor=ctor,HasPhysicalDefaultConstructor=physicalCtor,ValidAsTypeArgument=owner.RuntimeTypeArgumentAllowed,Parameters=parameters,
            BaseType=owner.BaseIndex<0?null:Shape(all[owner.BaseIndex].Id),Interfaces=owner.Interfaces.Select(i=>Shape(all[i].Id)).ToArray(),InstanceFields=owner.Fields.Select(f=>Shape(f.Type)).ToArray()
        }};
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarExpression TypeShapeOfExpression()
        {
            var keyword=Next();var previous=activeRuntimeQueryProof;var previousAnnotations=activeRuntimeQueryAnnotations;activeRuntimeQueryProof=null;activeRuntimeQueryAnnotations=[];
            if(runtimeQueryProofs?.TryGetValue(keyword.Location.Offset,out var declaration)==true)activeRuntimeQueryProof=declaration;
            try
            {
                Expect("(");var shape=ReadRuntimeTypeShape();Expect(")");
                var proof=BindRuntimeTypeProof(shape,keyword.Location);
                if(!_checkOnly)
                {
                    RememberRuntimeQueryShape(shape);
                    if(proof is not null)
                    {
                        RememberRuntimeQueryShape(proof.DeclaredShape);
                        foreach(var binding in proof.Bindings)RememberRuntimeQueryShape(binding);
                        foreach(var parameter in proof.Parameters)foreach(var bound in parameter.Bounds)RememberRuntimeQueryShape(bound);
                    }
                }
                if(TryStorageType(shape,out var named))return new("type-literal",NativeType(keyword.Location),keyword.Location,((int)named).ToString(CultureInfo.InvariantCulture));
                return new("type-shape-literal",NativeType(keyword.Location),keyword.Location){RuntimeTypeShape=shape,RuntimeTypeProof=proof};
            }
            finally{activeRuntimeQueryProof=previous;activeRuntimeQueryAnnotations=previousAnnotations;}
        }
        private void CloseRuntimeTypeArguments()
        {
            if(Current.Text.Length>1&&Current.Text.All(c=>c=='>'))
            {
                var token=Next();_tokens.Insert(_position,new(token.Text[1..],new(token.Location.Line,token.Location.Column+1,token.Location.Offset+1)));
            }
            else Expect(">");
        }
        private ScalarRuntimeTypeShape ReadRuntimeTypeShape(int depth=0)
        {
            var at=Current.Location;if(depth>=64)Fail("AI3100","Runtime type descriptions exceed the supported nesting depth.",at);
            var qualified=QualifiedBuiltinAt(_position);var token=qualified is null?Next():TypeHeadToken();var name=qualified?.Alias??token.Text;
            if(name.Contains('<')&&EnumDefinition(name.Split('<')[0]) is not null)
            {
                // Flattened qualifiers can include an entire generic enum name. Re-enter the
                // shape grammar so unbound slots and structural arguments keep their meaning.
                var parts=Lex(name,at,Has(ScalarFeatures.Floats)||Has(ScalarFeatures.Decimals),Has(ScalarFeatures.Chars),Has(ScalarFeatures.Strings));
                _tokens.InsertRange(_position,parts.Skip(1).Where(p=>p.Text!=""));name=parts[0].Text;token=new(name,at);
            }
            if(qualified is null&&name=="System"&&Take("."))
            {
                if(SourceSystemType())Fail("AI3106","The source type 'System' does not contain this qualified library type.",at);
                name+="."+Next().Text;
            }
            var canonical=name switch
            {
                "System.Int32"=>"int","System.Boolean"=>"bool","System.Void"=>"void","System.Int64"=>"long","System.UInt32"=>"uint","System.UInt64"=>"ulong",
                "System.Int16"=>"short","System.UInt16"=>"ushort","System.Byte"=>"byte","System.SByte"=>"sbyte","System.Single"=>"f32","System.Double"=>"f64",
                "System.Char"=>"char","System.String"=>"string","System.Decimal"=>"decimal","System.IntPtr"=>"nint","System.UIntPtr"=>"nuint","System.Object"=>"object",_=>name
            };
            var parameterName=(_memberArgs?.ContainsKey(name)??false)||(_typeArgs?.ContainsKey(name)??false);
            ScalarRuntimeTypeShape shape;var dynamicAlias=name=="dynamic"&&!parameterName&&StructNamed(name) is null;
            if(name=="(")shape=ReadRuntimeTuple(at,depth);
            else if(Take("<"))
            {
                var definition=StructNamedAny(name);var enumDefinition=EnumDefinition(name);var nullableName=name is "Nullable" or "System.Nullable";var valueTupleName=name is "ValueTuple" or "System.ValueTuple";
                if(Current.Text is "," or ">" or ">>" or ">>>")
                {
                    var arity=1;while(Take(","))arity++;CloseRuntimeTypeArguments();
                    var nullable=nullableName&&(definition is null||definition.TypeParameters.Count!=arity);var valueTuple=valueTupleName&&(definition is null||definition.TypeParameters.Count!=arity);
                    if(nullable)
                    {
                        if(arity!=1)Fail("AI3116","Nullable requires exactly one type parameter.",token.Location);
                        shape=new(ScalarRuntimeTypeKind.NullableDefinition,at);
                    }
                    else if(valueTuple)
                    {
                        if(arity>8)Fail("AI3116","ValueTuple has generic definitions of arity one through eight.",token.Location);
                        shape=new(ScalarRuntimeTypeKind.ValueTupleDefinition,at){Rank=arity};
                    }
                    else
                    {
                        if((definition?.TypeParameters.Count??enumDefinition?.TypeParameters.Count)!=arity)Fail("AI3116","The unbound generic type name does not match a declared generic arity.",token.Location);
                        shape=new(ScalarRuntimeTypeKind.Named,at){NamedType=definition?.Id??enumDefinition!.Id};
                    }
                    if(Current.Text is "[" or "?")Fail("AI3106","An unbound generic definition cannot be an element of a constructed type.",Current.Location);
                    return shape;
                }
                var arguments=new List<ScalarRuntimeTypeShape>();
                do{arguments.Add(ReadRuntimeTypeShape(depth+1));}while(Take(","));CloseRuntimeTypeArguments();
                if(enumDefinition is not null)shape=new(ScalarRuntimeTypeKind.Named,at){NamedType=EnumInstance(enumDefinition,arguments,at)};
                else if(nullableName&&(definition is null||definition.TypeParameters.Count!=arguments.Count))
                {
                    if(arguments.Count!=1)Fail("AI3116","Nullable requires exactly one type argument.",at);
                    shape=NullableShape(arguments[0],at,arguments[0].Location);
                }
                else if(valueTupleName&&(definition is null||definition.TypeParameters.Count!=arguments.Count))shape=RuntimeValueTuple(arguments,at);
                else
                {
                    if(definition is null||definition.TypeParameters.Count!=arguments.Count)Fail("AI3116","The generic type name does not match its declared arity.",at);
                    foreach(var argument in arguments)
                        if(!IsRuntimeElementType(argument)||argument.Kind==ScalarRuntimeTypeKind.Named&&IsStruct(argument.NamedType!.Value)&&StructOf(argument.NamedType.Value).IsCheckInstance&&StructOf(argument.NamedType.Value).TypeParameters.Count>0)
                            Fail("AI3106","A generic argument must be a closed non-void, non-static type.",argument.Location);
                    if(arguments.All(a=>TryStorageType(a,out _)))
                        shape=new(ScalarRuntimeTypeKind.Named,at){NamedType=InstanceOf(IndexOf(definition!.Id),arguments.Select(a=>{TryStorageType(a,out var t);return t;}).ToArray(),at)};
                    else
                    {
                        var views=_structs.Select(SaveRuntimeTypeDefinition).ToArray();
                        if(activeRuntimeQueryProof is null&&!ScalarRuntimeTypeConstraints.Check(definition!.Id,arguments,views,_enums,out var failed,out var reason))
                            Fail("AI3107",reason,failed>=0&&failed<arguments.Count?arguments[failed].Location:at);
                        shape=new(ScalarRuntimeTypeKind.Constructed,at){NamedType=definition!.Id,Arguments=arguments};
                    }
                }
            }
            else if(name is "ValueTuple" or "System.ValueTuple"&&!parameterName&&StructNamed(name) is null)shape=new(ScalarRuntimeTypeKind.ValueTupleDefinition,at);
            else shape=new(ScalarRuntimeTypeKind.Named,at){NamedType=dynamicAlias?(ScalarType)ObjectTypeId:Type(qualified is null?canonical:token.Text,at,allowUnit:true,allowStatic:true)};
            var suffixDepth=0;var forbiddenRootAnnotation=false;
            while(true)
            {
                if(Take("?"))
                {
                    var question=_tokens[_position-1].Location;
                    RuntimeQueryAnnotation annotation;
                    if(activeRuntimeQueryProof?.Annotations.TryGetValue(question.Offset,out var original)==true)annotation=original;
                    else
                    {
                        var value=RuntimeNonNullableValueType(shape);
                        var parameter=shape.Kind==ScalarRuntimeTypeKind.Named&&shape.NamedType is {} id&&IsTypeParameterType(id)?StructOf(id):null;
                        var reference=shape.Kind==ScalarRuntimeTypeKind.Array||shape.Kind==ScalarRuntimeTypeKind.Constructed&&StructOf(shape.NamedType!.Value).IsClass
                            ||shape.Kind==ScalarRuntimeTypeKind.Named&&shape.NamedType is {} named&&(named==ScalarType.String||IsObject(named)||IsClass(named)||parameter is not null);
                        if(!value&&(!reference||!IsRuntimeElementType(shape)))Fail("AI3106","A nullable annotation requires a value, reference or type-parameter type.",at);
                        annotation=new(value,value||parameter is {ReferenceKnown:false});
                    }
                    activeRuntimeQueryAnnotations![question.Offset]=annotation;
                    if(annotation.IsValue)shape=NullableShape(shape,at);
                    forbiddenRootAnnotation=!annotation.AllowsRoot;
                }
                var ranks=new List<(int Rank,SourceLocation At)>();
                while(Take("["))
                {
                    var rank=1;var rankAt=_tokens[_position-1].Location;while(Take(","))rank++;Expect("]");
                    if(rank>32)Fail("AI3100","An array type cannot exceed rank 32.",rankAt);ranks.Add((rank,rankAt));
                    if(++suffixDepth+depth>=64)Fail("AI3100","Runtime type descriptions exceed the supported nesting depth.",rankAt);
                }
                if(ranks.Count==0)break;
                if(!IsRuntimeElementType(shape)||shape.Kind==ScalarRuntimeTypeKind.Named&&IsStruct(shape.NamedType!.Value)&&StructOf(shape.NamedType.Value).IsCheckInstance&&StructOf(shape.NamedType.Value).TypeParameters.Count>0)
                    Fail("AI3106","An array element must be a closed non-void, non-static type.",at);
                // A nullable annotation ends a contiguous array-rank group. The next group
                // describes a new outer array; ranks within each group bind in reverse order.
                for(var i=ranks.Count-1;i>=0;i--)shape=new(ScalarRuntimeTypeKind.Array,ranks[i].At){Element=shape,Rank=ranks[i].Rank};
                forbiddenRootAnnotation=false;dynamicAlias=false;
                if(Current.Text!="?")break;
            }
            if(Current.Text=="?")Fail("AI3106","A nullable annotation cannot be repeated without an intervening array type.",Current.Location);
            if(depth==0&&dynamicAlias)Fail("AI3106","A type query cannot directly name dynamic; nested dynamic annotations have object identity.",at);
            if(depth==0&&forbiddenRootAnnotation)Fail("AI3106","A type query cannot directly name an annotated nullable reference type.",at);
            return shape;
        }
        private ScalarRuntimeTypeShape ReadRuntimeTuple(SourceLocation at,int depth)
        {
            var elements=new List<ScalarRuntimeTypeShape>();var names=new HashSet<string>(StringComparer.Ordinal);
            if(Current.Text==")")Fail("AI3106","A tuple type requires at least two elements.",at);
            do
            {
                if(elements.Count>=1024)Fail("AI3100","A tuple type exceeds its element limit.",Current.Location);
                var element=ReadRuntimeTypeShape(depth+1);elements.Add(element);
                if(!IsRuntimeElementType(element))Fail("AI3106","A tuple element must be a closed non-void, non-static type.",element.Location);
                if(Current.Text is not ("," or ")"))
                {
                    var label=Next();var name=label.Text;
                    if(!IsIdentifier(name)||IsReserved(name)||IsTypeKeyword(name)||name is "this" or "new" or "null" or "object" or "class" or "struct" or "interface" or "base" or "fn")
                        Fail("AI3106","Invalid tuple element name.",label.Location);
                    var item=name.StartsWith("Item",StringComparison.Ordinal)&&name.Length>4&&name[4] is >= '1' and <= '9'&&int.TryParse(name.AsSpan(4),NumberStyles.None,CultureInfo.InvariantCulture,out _);
                    if(name is "Rest" or "ToString" or "GetHashCode" or "Equals" or "CompareTo" or "Deconstruct"||item&&name!="Item"+elements.Count.ToString(CultureInfo.InvariantCulture))
                        Fail("AI3106","This tuple element name is reserved or does not match its position.",label.Location);
                    if(!names.Add(name))Fail("AI3106","Tuple element names must be unique.",label.Location);
                }
            }while(Take(","));Expect(")");
            if(elements.Count<2)Fail("AI3106","A tuple type requires at least two elements.",at);
            ScalarRuntimeTypeShape Pack(int start,int nesting)
            {
                if(depth+nesting>=64)Fail("AI3100","Runtime type descriptions exceed the supported nesting depth.",at);
                var count=Math.Min(7,elements.Count-start);var arguments=elements.Skip(start).Take(count).ToList();
                if(start+count<elements.Count)arguments.Add(Pack(start+count,nesting+1));
                return RuntimeValueTuple(arguments,at);
            }
            return Pack(0,0);
        }
        private ScalarRuntimeTypeShape RuntimeValueTuple(IReadOnlyList<ScalarRuntimeTypeShape> arguments,SourceLocation at)
        {
            if(arguments.Count is <1 or >8)Fail("AI3116","ValueTuple requires one through eight type arguments.",at);
            foreach(var argument in arguments)if(!IsRuntimeElementType(argument))Fail("AI3106","A tuple argument must be a closed non-void, non-static type.",argument.Location);
            if(arguments.Count==8&&!RuntimeNonNullableValueType(arguments[7]))Fail("AI3107","The eighth ValueTuple argument must be a non-nullable value type.",arguments[7].Location);
            if(activeRuntimeQueryProof is null&&!ScalarRuntimeTypeConstraints.CheckValueTuple(arguments,_structs.Select(SaveRuntimeTypeDefinition).ToArray(),_enums,out var failed,out var reason))
                Fail("AI3107",reason,failed>=0&&failed<arguments.Count?arguments[failed].Location:at);
            return new(ScalarRuntimeTypeKind.ValueTuple,at){Arguments=arguments};
        }
        private bool IsRuntimeElementType(ScalarRuntimeTypeShape shape)=>shape.Kind==ScalarRuntimeTypeKind.NullableDefinition||shape.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition&&shape.Rank!=0?false:shape.Kind==ScalarRuntimeTypeKind.Constructed?!StructOf(shape.NamedType!.Value).IsStatic
            :shape.Kind!=ScalarRuntimeTypeKind.Named||shape.NamedType is {} type&&type!=ScalarType.Unit&&(!IsEnum(type)||!EnumDefinitionType(EnumOf(type)))&&(!IsStruct(type)||!StructOf(type).IsStatic&&!(StructOf(type).IsCheckInstance&&StructOf(type).TypeParameters.Count>0));
        private bool RuntimeNonNullableValueType(ScalarRuntimeTypeShape shape)=>shape.Kind==ScalarRuntimeTypeKind.ValueTuple||shape.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition&&shape.Rank==0||shape.Kind==ScalarRuntimeTypeKind.Named&&shape.NamedType is {} type&&IsValueType(type)
            ||shape.Kind==ScalarRuntimeTypeKind.Constructed&&!StructOf(shape.NamedType!.Value).IsClass;
        private ScalarRuntimeTypeShape NullableShape(ScalarRuntimeTypeShape element,SourceLocation at,SourceLocation? argumentAt=null)
        {
            if(!RuntimeNonNullableValueType(element))Fail("AI3106","A nullable value type requires a non-nullable value-type argument.",argumentAt??at);
            return new(ScalarRuntimeTypeKind.Nullable,at){Element=element};
        }
        private bool TryStorageType(ScalarRuntimeTypeShape shape,out ScalarType type)
        {
            if(shape.Kind==ScalarRuntimeTypeKind.Named&&shape.NamedType is {} named){type=named;return true;}
            if(shape.Kind==ScalarRuntimeTypeKind.Array&&shape.Rank==1&&shape.Element is {Kind:ScalarRuntimeTypeKind.Named,NamedType:ScalarType.String}&&_structs.FirstOrDefault(s=>s.IsStringArray) is {} array){type=array.Id;return true;}
            type=default;return false;
        }
    }
}
