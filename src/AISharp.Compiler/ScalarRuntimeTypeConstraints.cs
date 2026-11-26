using System.Globalization;

namespace AISharp.Compiler;

/// <summary>Constraint proofs over logical types without manufacturing unsupported native storage layouts.</summary>
public static class ScalarRuntimeTypeConstraints
{
    internal static string IdentityKey(ScalarRuntimeTypeShape shape,IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums)=>new Engine(types,enums).Key(shape);
    public static bool CheckEnumMetadata(IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums,out string reason)
    {
        try{var engine=new Engine(types,enums);foreach(var enumeration in enums)engine.ValidateEnum(enumeration);reason="";return true;}
        catch(ProofFailure error){reason=error.Message;return false;}
    }
    public static bool CheckValueTuple(IReadOnlyList<ScalarRuntimeTypeShape> arguments,IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums,out int failedArgument,out string reason)
    {
        var engine=new Engine(types,enums);return engine.CheckValueTuple(arguments,out failedArgument,out reason);
    }
    public static bool Check(ScalarType definition,IReadOnlyList<ScalarRuntimeTypeShape> arguments,IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums,out int failedArgument,out string reason)
    {
        var engine=new Engine(types,enums);return engine.Check(definition,arguments,out failedArgument,out reason);
    }
    public static bool CheckProof(ScalarRuntimeTypeShape actual,ScalarRuntimeTypeProof proof,IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums,out string reason,bool allowOpenBindings=false)
    {
        try
        {
            if(proof is null||proof.Parameters is null||proof.Bindings is null||proof.DeclaredShape is null||proof.Parameters.Count==0||proof.Parameters.Count!=proof.Bindings.Count)throw new ProofFailure("A generic type-query proof has inconsistent parameter/binding metadata.");
            var symbolic=new Engine(types,enums,proof.Parameters);symbolic.ValidateParameters(proof.Parameters);symbolic.ValidateTree(proof.DeclaredShape);
            // Required-member restrictions were checked on the symbolic source declaration. The
            // closed runtime binding must still have a real public/default constructor; it cannot
            // replace a missing constructor or weaken the declared type constraints.
            var specialized=new Engine(types,enums,physicalConstructors:true,allowOpenNamed:allowOpenBindings);
            foreach(var binding in proof.Bindings)specialized.ValidateTree(binding);
            if(!specialized.CheckParameters(proof.Parameters,proof.Bindings,out _,out reason))return false;
            var substituted=specialized.Substitute(proof.DeclaredShape,proof.Bindings);
            specialized.ValidateTree(actual);
            if(specialized.Key(substituted)!=specialized.Key(actual))throw new ProofFailure("A generic type-query specialization does not match its checked declaration.");
            reason="";return true;
        }
        catch(ProofFailure error){reason=error.Message;return false;}
    }
    private sealed class ProofFailure(string message):Exception(message);
    private sealed class Engine(IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums,IReadOnlyList<ScalarRuntimeGenericParameter>? parameters=null,bool physicalConstructors=false,bool allowOpenNamed=true)
    {
        private int steps;
        private void Step(){if(++steps>16384)throw new ProofFailure("Logical type constraint proof exceeds its resource limit.");}
        private ScalarStruct? Nominal(ScalarType type)
        {
            var index=(int)type-ScalarLanguage.StructTypeBase;
            return type!=(ScalarType)ScalarLanguage.ObjectTypeId&&index>=0&&index<types.Count?types[index]:null;
        }
        private static ScalarRuntimeTypeShape Named(ScalarType type)=>new(ScalarRuntimeTypeKind.Named,default!){NamedType=type};
        private ScalarRuntimeGenericParameter Parameter(ScalarRuntimeTypeShape shape)
        {
            if(parameters is null||shape.Rank<0||shape.Rank>=parameters.Count||shape.NamedType is not null||shape.Element is not null||shape.Arguments is not null)throw new ProofFailure("A type-query parameter has no valid declaration proof.");
            return parameters[shape.Rank];
        }
        private ScalarRuntimeTypeShape Normalize(ScalarRuntimeTypeShape type)
        {
            Step();if(type.Kind!=ScalarRuntimeTypeKind.Named||type.NamedType is not {} id)return type;
            if(enums.FirstOrDefault(e=>e.Id==id) is {Template:>=0,TypeArguments.Count:>0} enumeration&&enumeration.Template!=(int)id-ScalarLanguage.EnumTypeBase)
                return new(ScalarRuntimeTypeKind.Constructed,type.Location){NamedType=enums[enumeration.Template].Id,Arguments=enumeration.TypeArguments};
            var info=Nominal(id);if(info is {IsStringArray:true})return new(ScalarRuntimeTypeKind.Array,type.Location){Rank=1,Element=Named(ScalarType.String)};
            if(info is {Template:>=0,TypeArgumentList.Count:>0}&&info.Template!=(int)id-ScalarLanguage.StructTypeBase)
                return new(ScalarRuntimeTypeKind.Constructed,type.Location){NamedType=types[info.Template].Id,Arguments=info.TypeArgumentList.Select(Named).ToArray()};
            return type;
        }
        public string Key(ScalarRuntimeTypeShape raw,int depth=0)
        {
            if(depth>=64)throw new ProofFailure("Logical type constraint descriptions exceed their nesting limit.");var type=Normalize(raw);
            return type.Kind switch
            {
                ScalarRuntimeTypeKind.Named when type.NamedType is {} id=>"n"+((int)id).ToString(CultureInfo.InvariantCulture),
                ScalarRuntimeTypeKind.Array=>"a"+type.Rank.ToString(CultureInfo.InvariantCulture)+"("+Key(type.Element!,depth+1)+")",
                ScalarRuntimeTypeKind.Nullable=>"q("+Key(type.Element!,depth+1)+")",
                ScalarRuntimeTypeKind.NullableDefinition=>"qd",
                ScalarRuntimeTypeKind.ValueTupleDefinition=>"td"+type.Rank.ToString(CultureInfo.InvariantCulture),
                ScalarRuntimeTypeKind.ValueTuple=>"t("+string.Join(",",type.Arguments!.Select(a=>Key(a,depth+1)))+")",
                ScalarRuntimeTypeKind.Constructed=>"g"+((int)type.NamedType!.Value).ToString(CultureInfo.InvariantCulture)+"("+string.Join(",",type.Arguments!.Select(a=>Key(a,depth+1)))+")",
                ScalarRuntimeTypeKind.Parameter when Parameter(type) is not null=>"p"+type.Rank.ToString(CultureInfo.InvariantCulture),
                _=>throw new ProofFailure("A logical constraint contains unresolved or invalid type metadata.")
            };
        }
        public ScalarRuntimeTypeShape Substitute(ScalarRuntimeTypeShape shape,IReadOnlyList<ScalarRuntimeTypeShape> arguments,int depth=0)
        {
            Step();if(shape is null||depth>=64)throw new ProofFailure("Logical constraint substitution is null or exceeds its nesting limit.");
            if(shape.Kind==ScalarRuntimeTypeKind.Parameter)
            {
                if(shape.Rank<0||shape.Rank>=arguments.Count||shape.NamedType is not null||shape.Element is not null||shape.Arguments is not null)throw new ProofFailure("Logical parameter substitution metadata is invalid.");
                return arguments[shape.Rank];
            }
            return shape with{Element=shape.Element is null?null:Substitute(shape.Element,arguments,depth+1),Arguments=shape.Arguments?.Select(a=>Substitute(a,arguments,depth+1)).ToArray()};
        }
        private ScalarRuntimeGenericDefinition Metadata(ScalarType type)
        {
            if(enums.FirstOrDefault(e=>e.Id==type) is {} enumeration)
            {
                var enumModel=enumeration.RuntimeTypeDefinition??throw new ProofFailure("Generic enum declaration proof metadata is missing.");
                var parent=enumeration.GenericOwner is {} ownerId?Nominal(ownerId):null;
                var parentModel=parent?.RuntimeTypeDefinition;
                if(enumeration.Template!=(int)type-ScalarLanguage.EnumTypeBase||enumModel.Arity==0||enumModel.Arity!=enumeration.TypeParameters.Count||enumModel.Parameters is null||enumModel.Parameters.Any(p=>p is null)||enumModel.Parameters.Count!=enumModel.Arity||!enumModel.Parameters.Select(p=>p.Name).SequenceEqual(enumeration.TypeParameters)
                    ||enumModel.IsClass||enumModel.IsInterface||enumModel.IsStatic||!enumModel.HasDefaultConstructor||!enumModel.HasPhysicalDefaultConstructor||!enumModel.ValidAsTypeArgument||enumModel.BaseType is not null||enumModel.Interfaces is null||enumModel.Interfaces.Count!=0||enumModel.InstanceFields is null||enumModel.InstanceFields.Count!=0
                    ||parentModel is null||parentModel.Parameters is null||parentModel.Parameters.Count!=enumModel.Arity||parentModel.Arity!=enumModel.Arity||parentModel.HasConstraints!=enumModel.HasConstraints)
                    throw new ProofFailure("Generic enum metadata does not match its containing declaration.");
                ValidateParameters(enumModel.Parameters);ValidateParameters(parentModel.Parameters);
                for(var i=0;i<enumModel.Arity;i++)
                {
                    var a=enumModel.Parameters[i];var b=parentModel.Parameters[i];
                    if(a.Reference!=b.Reference||a.Value!=b.Value||a.Unmanaged!=b.Unmanaged||a.Constructor!=b.Constructor||!a.Dependencies.SequenceEqual(b.Dependencies)||a.Bounds.Count!=b.Bounds.Count||!a.Bounds.Zip(b.Bounds).All(p=>SameDeclarationShape(p.First,p.Second)))throw new ProofFailure("A generic enum cannot replace its containing constraints.");
                }
                return enumModel;
            }
            var owner=Nominal(type)??throw new ProofFailure("A logical generic definition is not a bound nominal type.");var model=owner.RuntimeTypeDefinition??throw new ProofFailure("Logical generic declaration proof metadata is missing.");
            if(owner.Template!=(int)type-ScalarLanguage.StructTypeBase||model.Arity==0||model.Arity!=owner.TypeParameters.Count||model.Parameters is null||model.Parameters.Any(p=>p is null)||model.Parameters.Count!=model.Arity||!model.Parameters.Select(p=>p.Name).SequenceEqual(owner.TypeParameters)
                ||model.ValidAsTypeArgument!=owner.RuntimeTypeArgumentAllowed||model.IsInterface&&!model.IsClass||model.IsStatic&&(!model.IsClass||model.IsInterface)||model.HasDefaultConstructor&&!model.HasPhysicalDefaultConstructor
                ||(model.HasDefaultConstructor||model.HasPhysicalDefaultConstructor)&&(model.IsInterface||model.IsStatic))
                throw new ProofFailure("Logical generic declaration parameter metadata is incomplete or inconsistent.");
            ValidateParameters(model.Parameters);return model;
        }
        public void ValidateEnum(ScalarEnum enumeration)
        {
            Step();var index=(int)enumeration.Id-ScalarLanguage.EnumTypeBase;
            if(index<0||index>=enums.Count||!ReferenceEquals(enums[index],enumeration)||index>=1000||enumeration.TypeParameters is null||enumeration.TypeArguments is null)throw new ProofFailure("Enum identity or generic metadata is invalid.");
            if(enumeration.TypeParameters.Count==0)
            {
                if(enumeration.Template!=-1||enumeration.GenericOwner is not null||enumeration.TypeArguments.Count!=0||enumeration.IsCheckInstance||enumeration.RuntimeTypeDefinition is not null)throw new ProofFailure("A nongeneric enum cannot carry generic metadata.");return;
            }
            if(enumeration.Template<0||enumeration.Template>=enums.Count||enumeration.SourceOwnArity!=0)throw new ProofFailure("A generic enum must capture a containing declaration without own type parameters.");
            var definition=enums[enumeration.Template];_=Metadata(definition.Id);
            if(!enumeration.TypeParameters.SequenceEqual(definition.TypeParameters)||enumeration.GenericOwner!=definition.GenericOwner||enumeration.Underlying!=definition.Underlying||!enumeration.Members.SequenceEqual(definition.Members)||enumeration.SourceFullName!=definition.SourceFullName)throw new ProofFailure("A constructed enum cannot replace its definition metadata.");
            if(index==enumeration.Template)
            {
                if(!enumeration.IsCheckInstance||enumeration.TypeArguments.Count!=0)throw new ProofFailure("An enum definition cannot carry constructed arguments.");return;
            }
            if(enumeration.TypeArguments.Count!=definition.TypeParameters.Count)throw new ProofFailure("An enum construction has invalid captured arity.");
            bool Open(ScalarRuntimeTypeShape shape,int depth=0)
            {
                Step();if(depth>=64)throw new ProofFailure("Enum argument openness exceeds its nesting limit.");
                if(shape.Kind==ScalarRuntimeTypeKind.Parameter)return true;
                if(shape.NamedType is {} named&&(Nominal(named) is {IsTypeParameter:true} or {IsCheckInstance:true}||enums.Any(e=>e.Id==named&&e.IsCheckInstance)))return true;
                return shape.Element is not null&&Open(shape.Element,depth+1)||shape.Arguments?.Any(a=>Open(a,depth+1))==true;
            }
            foreach(var argument in enumeration.TypeArguments)ValidateTree(argument);
            if(enumeration.IsCheckInstance!=enumeration.TypeArguments.Any(a=>Open(a)))throw new ProofFailure("An enum construction cannot change its open/closed classification.");
            if(!Check(definition.Id,enumeration.TypeArguments,out _,out var failure))throw new ProofFailure(failure);
        }
        private bool SameDeclarationShape(ScalarRuntimeTypeShape? a,ScalarRuntimeTypeShape? b,int depth=0)
        {
            Step();if(depth>=64)throw new ProofFailure("Generic enum declaration proof exceeds its nesting limit.");
            if(a is null||b is null)return a is null&&b is null;
            return a.Kind==b.Kind&&a.NamedType==b.NamedType&&a.Rank==b.Rank&&SameDeclarationShape(a.Element,b.Element,depth+1)
                &&(a.Arguments is null||b.Arguments is null?a.Arguments is null&&b.Arguments is null:a.Arguments.Count==b.Arguments.Count&&a.Arguments.Zip(b.Arguments).All(p=>SameDeclarationShape(p.First,p.Second,depth+1)));
        }
        public void ValidateParameters(IReadOnlyList<ScalarRuntimeGenericParameter> proof)
        {
            if(proof is null||proof.Count>1024)throw new ProofFailure("Generic proof parameters are missing or exceed their limit.");
            foreach(var p in proof)
                if(p is null||p.Bounds is null||p.Dependencies is null||p.Reference&&p.Value||p.Unmanaged&&!p.Value||p.Dependencies.Any(i=>i<0||i>=proof.Count))throw new ProofFailure("Logical generic parameter proof metadata is inconsistent.");
            var state=new int[proof.Count];
            void Visit(int index)
            {
                if(state[index]==1)throw new ProofFailure("Logical generic parameter dependencies are cyclic.");if(state[index]==2)return;state[index]=1;
                foreach(var next in proof[index].Dependencies)Visit(next);state[index]=2;
            }
            for(var i=0;i<proof.Count;i++)Visit(i);
        }
        private bool Reference(ScalarRuntimeTypeShape raw)
        {
            var type=Normalize(raw);if(type.Kind==ScalarRuntimeTypeKind.Array)return true;
            if(type.Kind==ScalarRuntimeTypeKind.Parameter)return Parameter(type).Reference;
            if(type.Kind==ScalarRuntimeTypeKind.Constructed)return Metadata(type.NamedType!.Value).IsClass;
            if(type.Kind!=ScalarRuntimeTypeKind.Named)return false;
            if(type.NamedType is ScalarType.String||type.NamedType==(ScalarType)ScalarLanguage.ObjectTypeId)return true;
            var owner=Nominal(type.NamedType!.Value);return owner is not null&&(owner.IsTypeParameter?owner.ReferenceKnown:owner.IsClass);
        }
        private bool NonNullableValue(ScalarRuntimeTypeShape raw)
        {
            var type=Normalize(raw);if(type.Kind==ScalarRuntimeTypeKind.ValueTuple||type.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition&&type.Rank==0)return true;
            if(type.Kind==ScalarRuntimeTypeKind.Constructed){var model=Metadata(type.NamedType!.Value);return !model.IsClass&&!model.IsStatic;}
            if(type.Kind==ScalarRuntimeTypeKind.Parameter)return Parameter(type).Value;
            if(type.Kind!=ScalarRuntimeTypeKind.Named||type.NamedType is not {} id||id is ScalarType.Unit or ScalarType.String||id==(ScalarType)ScalarLanguage.ObjectTypeId)return false;
            if(Enum.IsDefined(id))return true;if(enums.FirstOrDefault(e=>e.Id==id) is {} enumeration)return enumeration.TypeParameters.Count==0;var owner=Nominal(id);
            return owner is not null&&(owner.IsTypeParameter?owner.HasValueConstraint:!owner.IsClass&&!owner.IsCheckInstance);
        }
        private bool NoManagedReferences(ScalarRuntimeTypeShape raw,HashSet<string> visiting,int depth=0)
        {
            if(depth>=64)throw new ProofFailure("Unmanaged type proof exceeds its nesting limit.");var type=Normalize(raw);var key=Key(type);if(!visiting.Add(key))return false;
            try
            {
                // Nullable storage can contain no references even though a nullable root does not
                // satisfy the non-nullable 'unmanaged' constraint.
                if(type.Kind==ScalarRuntimeTypeKind.Nullable)return NoManagedReferences(type.Element!,visiting,depth+1);
                if(type.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition)return type.Rank==0;
                if(type.Kind==ScalarRuntimeTypeKind.ValueTuple)return type.Arguments!.All(a=>NoManagedReferences(a,visiting,depth+1));
                if(type.Kind==ScalarRuntimeTypeKind.Parameter)return Parameter(type).Unmanaged;
                if(type.Kind==ScalarRuntimeTypeKind.Constructed)
                {
                    var model=Metadata(type.NamedType!.Value);return !model.IsClass&&model.InstanceFields.All(f=>NoManagedReferences(Substitute(f,type.Arguments!),visiting,depth+1));
                }
                if(type.Kind!=ScalarRuntimeTypeKind.Named||type.NamedType is not {} id||!NonNullableValue(type))return false;
                if(Enum.IsDefined(id)||enums.Any(e=>e.Id==id))return true;var owner=Nominal(id)!;
                return owner.IsTypeParameter?owner.HasUnmanagedConstraint:owner.Fields.All(f=>NoManagedReferences(Named(f.Type),visiting,depth+1));
            }
            finally{visiting.Remove(key);}
        }
        private bool Constructor(ScalarRuntimeTypeShape raw)
        {
            var type=Normalize(raw);if(type.Kind is ScalarRuntimeTypeKind.Nullable or ScalarRuntimeTypeKind.ValueTuple||type.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition&&type.Rank==0)return true;
            if(type.Kind==ScalarRuntimeTypeKind.Parameter){var p=Parameter(type);return p.Value||p.Constructor;}
            if(type.Kind==ScalarRuntimeTypeKind.Constructed){var m=Metadata(type.NamedType!.Value);return physicalConstructors?m.HasPhysicalDefaultConstructor:m.HasDefaultConstructor;}
            if(type.Kind!=ScalarRuntimeTypeKind.Named||type.NamedType is not {} id)return false;
            if(id==(ScalarType)ScalarLanguage.ObjectTypeId)return true;var owner=Nominal(id);if(owner is null)return NonNullableValue(type);
            if(owner.IsTypeParameter)return owner.HasConstructorConstraint||owner.HasValueConstraint;
            if(!physicalConstructors&&(owner.Fields.Any(f=>f.IsRequired)||owner.Properties.Any(p=>p.IsRequired)))return false;
            if(!owner.IsClass)return true;
            if(owner.IsAbstract||owner.IsInterface||owner.IsStatic||owner.IsStringArray||owner.IsDelegate)return false;
            return owner.Methods.Any(m=>m.IsConstructor)?owner.Methods.Any(m=>m.IsConstructor&&m.IsPublic&&!m.IsInternalConstructor&&m.Declaration.Parameters.Count==0):!owner.IsRecord||owner.Positional==0;
        }
        private bool AllowedArgument(ScalarRuntimeTypeShape raw)
        {
            var type=Normalize(raw);if(type.Kind is ScalarRuntimeTypeKind.Array or ScalarRuntimeTypeKind.Nullable or ScalarRuntimeTypeKind.ValueTuple||type.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition&&type.Rank==0)return true;
            if(type.Kind==ScalarRuntimeTypeKind.Parameter){_=Parameter(type);return true;}
            if(type.Kind==ScalarRuntimeTypeKind.Constructed){var model=Metadata(type.NamedType!.Value);return !model.IsStatic&&model.ValidAsTypeArgument;}
            if(type.Kind!=ScalarRuntimeTypeKind.Named||type.NamedType is not {} id||id==ScalarType.Unit)return false;
            var owner=Nominal(id);return owner is null?Enum.IsDefined(id)||enums.Any(e=>e.Id==id&&e.TypeParameters.Count==0)||id==(ScalarType)ScalarLanguage.ObjectTypeId
                :!owner.IsStatic&&!owner.IsCheckInstance&&owner.RuntimeTypeArgumentAllowed||owner.IsTypeParameter;
        }
        private IEnumerable<ScalarRuntimeTypeShape> Parents(ScalarRuntimeTypeShape raw)
        {
            var type=Normalize(raw);
            if(type.Kind==ScalarRuntimeTypeKind.Parameter)
            {
                var p=Parameter(type);foreach(var bound in p.Bounds)yield return bound;
                foreach(var dependency in p.Dependencies)yield return new(ScalarRuntimeTypeKind.Parameter,type.Location){Rank=dependency};
            }
            else if(type.Kind==ScalarRuntimeTypeKind.Constructed)
            {
                var model=Metadata(type.NamedType!.Value);if(model.BaseType is {} parent)yield return Substitute(parent,type.Arguments!);
                foreach(var i in model.Interfaces)yield return Substitute(i,type.Arguments!);
            }
            else if(type.Kind==ScalarRuntimeTypeKind.Named&&type.NamedType is {} id&&Nominal(id) is {} owner)
            {
                if(owner.BaseIndex>=0)yield return Named(types[owner.BaseIndex].Id);
                foreach(var i in owner.Interfaces)yield return Named(types[i].Id);
                if(owner.IsTypeParameter&&owner.ParameterBounds is {} bounds)foreach(var bound in bounds.Dependencies)yield return Named(bound.Type);
            }
        }
        private bool Conversion(ScalarRuntimeTypeShape source,ScalarRuntimeTypeShape target,HashSet<string> visited,int depth=0)
        {
            Step();if(depth>=64)throw new ProofFailure("Logical type conversion proof exceeds its nesting limit.");source=Normalize(source);target=Normalize(target);
            var sourceKey=Key(source);var targetKey=Key(target);if(sourceKey==targetKey)return true;
            if(target.Kind==ScalarRuntimeTypeKind.Named&&target.NamedType==(ScalarType)ScalarLanguage.ObjectTypeId)return AllowedArgument(source);
            var edge=sourceKey+" -> "+targetKey;if(!visited.Add(edge))return false;
            try
            {
                if(source.Kind==ScalarRuntimeTypeKind.Array&&target.Kind==ScalarRuntimeTypeKind.Array&&source.Rank==target.Rank)
                    return Reference(source.Element!)&&Reference(target.Element!)&&Conversion(source.Element!,target.Element!,visited,depth+1);
                if(source.Kind==ScalarRuntimeTypeKind.Constructed&&target.Kind==ScalarRuntimeTypeKind.Constructed&&source.NamedType==target.NamedType)
                {
                    var owner=Nominal(source.NamedType!.Value);var model=Metadata(source.NamedType.Value);
                    if(owner is not null&&(model.IsInterface||owner.IsDelegate)&&owner.TypeVariances.Count==source.Arguments!.Count)
                    {
                        var match=true;
                        for(var i=0;i<source.Arguments!.Count;i++)
                        {
                            var a=source.Arguments[i];var b=target.Arguments![i];if(Key(a)==Key(b))continue;
                            if(owner.TypeVariances[i]==0||!Reference(a)||!Reference(b)||!(owner.TypeVariances[i]>0?Conversion(a,b,visited,depth+1):Conversion(b,a,visited,depth+1))){match=false;break;}
                        }
                        if(match)return true;
                    }
                }
                return Parents(source).Any(parent=>Conversion(parent,target,visited,depth+1));
            }
            finally{visited.Remove(edge);}
        }
        public bool Check(ScalarType definition,IReadOnlyList<ScalarRuntimeTypeShape> arguments,out int failedArgument,out string reason)
        {
            try
            {
                var model=Metadata(definition);if(model.Arity!=arguments.Count)throw new ProofFailure("Logical generic argument arity does not match its declaration.");
                return CheckParameters(model.Parameters,arguments,out failedArgument,out reason);
            }
            catch(ProofFailure error){failedArgument=-1;reason=error.Message;return false;}
        }
        public bool CheckParameters(IReadOnlyList<ScalarRuntimeGenericParameter> proofParameters,IReadOnlyList<ScalarRuntimeTypeShape> arguments,out int failedArgument,out string reason)
        {
            failedArgument=-1;reason="";
            try
            {
                ValidateParameters(proofParameters);if(proofParameters.Count!=arguments.Count)throw new ProofFailure("Logical proof argument arity is inconsistent.");
                for(var i=0;i<arguments.Count;i++)
                {
                    failedArgument=i;var argument=arguments[i];var proof=proofParameters[i];var valid=AllowedArgument(argument);
                    if(proof.Reference)valid&=Reference(argument);
                    if(proof.Value)valid&=NonNullableValue(argument);
                    if(proof.Unmanaged)valid&=NonNullableValue(argument)&&NoManagedReferences(argument,[]);
                    if(proof.Constructor)valid&=Constructor(argument);
                    foreach(var bound in proof.Bounds)valid&=Conversion(argument,Substitute(bound,arguments),[]);
                    foreach(var dependency in proof.Dependencies)valid&=Conversion(argument,arguments[dependency],[]);
                    if(!valid){reason="The logical type argument does not satisfy the constraints of '"+proof.Name+"'.";return false;}
                }
                failedArgument=-1;return true;
            }
            catch(ProofFailure error){reason=error.Message;return false;}
        }
        public bool CheckValueTuple(IReadOnlyList<ScalarRuntimeTypeShape> arguments,out int failedArgument,out string reason,int depth=0)
        {
            failedArgument=-1;reason="";
            try
            {
                Step();if(arguments is null||arguments.Count is <1 or >8)throw new ProofFailure("A ValueTuple description requires one through eight arguments.");
                for(var i=0;i<arguments.Count;i++)
                {
                    failedArgument=i;ValidateTree(arguments[i],depth+1);
                    if(!AllowedArgument(arguments[i]))throw new ProofFailure("A ValueTuple argument is not a valid closed generic argument.");
                }
                if(arguments.Count==8&&!NonNullableValue(arguments[7])){failedArgument=7;throw new ProofFailure("The eighth ValueTuple argument must be a non-nullable value type.");}
                failedArgument=-1;return true;
            }
            catch(ProofFailure error){reason=error.Message;return false;}
        }
        public void ValidateTree(ScalarRuntimeTypeShape raw,int depth=0)
        {
            Step();if(raw is null||depth>=64)throw new ProofFailure("A generic type-query proof is null, cyclic or too deep.");
            if(raw.Kind==ScalarRuntimeTypeKind.Named&&raw is not {NamedType:not null,Element:null,Rank:0,Arguments:null})throw new ProofFailure("A generic type-query proof has malformed named metadata.");
            var type=Normalize(raw);
            if(type.Kind==ScalarRuntimeTypeKind.NullableDefinition)
            {
                if(type is not {NamedType:null,Element:null,Rank:0,Arguments:null})throw new ProofFailure("An unbound Nullable definition cannot carry children, rank or nominal metadata.");return;
            }
            if(type.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition)
            {
                if(type is not {NamedType:null,Element:null,Arguments:null}||type.Rank is <0 or >8)throw new ProofFailure("A ValueTuple definition has invalid arity or structural metadata.");return;
            }
            if(type.Kind==ScalarRuntimeTypeKind.ValueTuple)
            {
                if(type is not {NamedType:null,Element:null,Rank:0,Arguments:not null})throw new ProofFailure("A ValueTuple type has invalid structural metadata.");
                if(!CheckValueTuple(type.Arguments,out _,out var tupleFailure,depth))throw new ProofFailure(tupleFailure);return;
            }
            if(type.Kind==ScalarRuntimeTypeKind.Named)
            {
                if(type is not {NamedType:not null,Element:null,Rank:0,Arguments:null})throw new ProofFailure("A generic type-query proof has malformed named metadata.");
                var id=type.NamedType.Value;var owner=Nominal(id);
                if(!Enum.IsDefined(id)&&id!=(ScalarType)ScalarLanguage.ObjectTypeId&&!enums.Any(e=>e.Id==id)&&owner is null||!allowOpenNamed&&owner is {IsTypeParameter:true})throw new ProofFailure("A generic type-query proof references an unknown or unbound type.");return;
            }
            if(type.Kind==ScalarRuntimeTypeKind.Parameter){_=Parameter(type);return;}
            if(type.Kind is ScalarRuntimeTypeKind.Array or ScalarRuntimeTypeKind.Nullable)
            {
                if(type is not {NamedType:null,Element:not null,Arguments:null}||type.Kind==ScalarRuntimeTypeKind.Array&&type.Rank is <1 or >32||type.Kind==ScalarRuntimeTypeKind.Nullable&&type.Rank!=0)throw new ProofFailure("A generic type-query proof has malformed element metadata.");
                ValidateTree(type.Element,depth+1);
                if(type.Kind==ScalarRuntimeTypeKind.Nullable&&!NonNullableValue(type.Element))throw new ProofFailure("A nullable proof element must be a non-nullable value type.");
                if(type.Kind==ScalarRuntimeTypeKind.Array&&type.Element.Kind!=ScalarRuntimeTypeKind.Parameter)
                {
                    var element=Normalize(type.Element);var owner=element.NamedType is {} id?Nominal(id):null;
                    if(element.Kind==ScalarRuntimeTypeKind.NullableDefinition||element.Kind==ScalarRuntimeTypeKind.ValueTupleDefinition&&element.Rank!=0||element.Kind==ScalarRuntimeTypeKind.Named&&(element.NamedType==ScalarType.Unit||owner is {IsStatic:true}||owner is {IsCheckInstance:true,TypeParameters.Count:>0})||element.Kind==ScalarRuntimeTypeKind.Constructed&&Metadata(element.NamedType!.Value).IsStatic)throw new ProofFailure("An array proof has an invalid element type.");
                }
                return;
            }
            if(type.Kind!=ScalarRuntimeTypeKind.Constructed||type is not {NamedType:not null,Element:null,Rank:0,Arguments:not null})throw new ProofFailure("A generic type-query proof contains invalid structural metadata.");
            foreach(var argument in type.Arguments)ValidateTree(argument,depth+1);
            if(!Check(type.NamedType.Value,type.Arguments,out _,out var reason))throw new ProofFailure(reason);
        }
    }
}
