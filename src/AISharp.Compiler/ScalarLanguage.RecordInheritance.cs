using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static Dictionary<(int Function,string Scope,int Call),string>? checkedRecordEqualityTargets;
    private static string RecordEqualityMethodName(ScalarStruct owner)=>"$record_equal_fields_"+StaticPropertyDeclaration(owner).ToString(CultureInfo.InvariantCulture);
    public static ScalarStructMethod? CustomRecordEquality(ScalarStruct owner)=>owner.Methods.FirstOrDefault(m=>m.IsRecordCustomEquality);
    private static ScalarStructMethod RecordEqualityMethod(ScalarStruct owner,int index)
    {
        var at=owner.Location;var type=owner.TypeParameters.Count>0&&!owner.Name.Contains('<')?owner.Name+"<"+string.Join(", ",owner.TypeParameters)+">":owner.Name;
        var declaration=new FunctionDeclaration("__record_fields_equal",[new("__record_left",type),new("__record_right",type)],"bool",[],[new(ContractKind.Ensures,"true",at)],new FunctionBody([]){ScalarSource=new("",at)},[],at);
        return new(RecordEqualityMethodName(owner),owner.Name+"."+RecordEqualityMethodName(owner),false,false,declaration){Owner=index,IsStatic=true,IsRecordEqualityComparer=true};
    }
    private static IEnumerable<ScalarExpression> RecordEqualityNodes(IEnumerable<ScalarFunction> functions)
    {
        var result=new List<ScalarExpression>();
        void Value(ScalarExpression? expression)
        {
            if(expression is null)return;if(expression.Operation is "record-equal" or "record-equals" or "record-impl-equals")result.Add(expression);
            Value(expression.Left);Value(expression.Right);if(expression.Arguments is not null)foreach(var argument in expression.Arguments)Value(argument);
        }
        void Statements(IReadOnlyList<ScalarStatement> statements)
        {
            foreach(var statement in statements){Value(statement.Expression);Value(statement.Increment);if(statement.Initializer is not null)Statements([statement.Initializer]);if(statement.Body is not null)Statements(statement.Body);if(statement.Alternative is not null)Statements(statement.Alternative);}
        }
        foreach(var function in functions){Statements(function.Body);foreach(var contract in function.Contracts)Value(contract.Expression);}return result;
    }
    private static bool EmitRecordEqualityComparers(List<ScalarFunction> members,ScalarFeatures features,IReadOnlyList<FunctionDeclaration> functions,IReadOnlyList<ScalarFunction> topLevel)
    {
        if((features&ScalarFeatures.RecordInheritance)==0)return false;var emitted=false;
        foreach(var node in RecordEqualityNodes(topLevel.Concat(members).Concat(liftedFunctions!)).ToArray())
        foreach(var concrete in node.Operation=="record-impl-equals"?new[]{IndexOf(node.Left!.Type)}:ConcreteOf(activeStructs!,IndexOf(node.Left!.Type)).ToArray())
        for(var index=concrete;index>=0&&activeStructs![index].IsRecord;index=activeStructs[index].BaseIndex)
        {
            var owner=activeStructs![index];var method=owner.Methods.Single(m=>m.IsRecordEqualityComparer);
            if(!requestedInstances!.Add(method.QualifiedName))continue;
            members.Add(new ExtendedBinder(method.Declaration,features,functions,owner,method,false).Bind());emitted=true;
        }
        return emitted;
    }
    private static bool IsRecordDeclaration(string source,int start)
    {
        while(IsWordAt(source,start,"abstract")||IsWordAt(source,start,"sealed")||IsWordAt(source,start,"static")||IsWordAt(source,start,"readonly"))
        {
            while(start<source.Length&&char.IsAsciiLetter(source[start]))start++;
            while(start<source.Length&&source[start] is ' ' or '\t')start++;
        }
        return IsWordAt(source,start,"record");
    }
    private static bool RecordMemberHiding(ScalarStruct owner)=>owner.IsRecord&&owner.BaseIndex>=0&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0;
    private static ScalarStructMethod? FindVirtualImplementation(int index,ScalarStructMethod root)
    {
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)
            if(activeStructs![k].Methods.FirstOrDefault(m=>m.Slot==root.Slot) is { } method)return method;
        return null;
    }
    private static void ValidateRecordEqualityShape(IReadOnlyList<ScalarStruct> all,ScalarType type,HashSet<ScalarType> stack,SourceLocation at)
    {
        if((int)type<StructTypeBase||IndexOf(type)>=all.Count)return;
        var owner=all[IndexOf(type)];if(owner.IsTypeParameter||CustomRecordEquality(owner)is not null)return;
        if(!stack.Add(type))throw DeclarationError("AI3100",$"Equality of the recursive record '{owner.Name}' is not supported yet.",at);
        try
        {
            foreach(var field in owner.Fields)
            {
                if(owner.BaseIndex>=0&&owner.IsRecord&&field.Owner!=IndexOf(owner.Id))continue;
                if((int)field.Type==ObjectTypeId||IndexOf(field.Type)>=0&&IndexOf(field.Type)<all.Count&&all[IndexOf(field.Type)].IsInterface)
                    throw DeclarationError("AI3100",$"Record equality over the {field.Type} member '{owner.Name}.{field.Name}' is not supported yet.",at);
                if(IndexOf(field.Type)>=0&&IndexOf(field.Type)<all.Count)
                {
                    var nested=all[IndexOf(field.Type)];
                    if(nested.IsRecord||!nested.IsClass)ValidateRecordEqualityShape(all,field.Type,stack,at);
                }
            }
            if(owner.BaseIndex>=0&&owner.IsRecord)ValidateRecordEqualityShape(all,all[owner.BaseIndex].Id,stack,at);
        }
        finally{stack.Remove(type);}
    }
    private static void ValidateRecordEqualityFamilies(IReadOnlyList<ScalarFunction> functions)
    {
        if((activeFeatures&ScalarFeatures.RecordInheritance)==0)return;
        void Value(ScalarExpression? expression)
        {
            if(expression is null)return;
            if(expression.Operation is "record-equal" or "record-equals" or "record-impl-equals"&&expression.Left is { } left)
                foreach(var k in expression.Operation=="record-impl-equals"?new[]{IndexOf(left.Type)}:ConcreteOf(activeStructs!,IndexOf(left.Type)))ValidateRecordEqualityShape(activeStructs!,activeStructs![k].Id,[],expression.Location);
            Value(expression.Left);Value(expression.Right);if(expression.Arguments is not null)foreach(var argument in expression.Arguments)Value(argument);
        }
        void Statements(IReadOnlyList<ScalarStatement> statements)
        {
            foreach(var statement in statements){Value(statement.Expression);Value(statement.Increment);if(statement.Initializer is not null)Statements([statement.Initializer]);if(statement.Body is not null)Statements(statement.Body);if(statement.Alternative is not null)Statements(statement.Alternative);}
        }
        foreach(var function in functions){Statements(function.Body);foreach(var contract in function.Contracts)Value(contract.Expression);}
    }
    private static (ScalarProperty? Property,ScalarStructField? Field) ReadableInheritedRecordMember(int index,string name,SourceLocation at)
    {
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)
        {
            var parent=activeStructs![k];var property=parent.Properties.FirstOrDefault(p=>p.Name==name&&p.IsPublic);
            if(property is not null)
            {
                if(property.IsStatic||!property.HasGet||property.GetPrivate)throw DeclarationError("AI3106","An inherited positional member must be a readable instance field or property.",at);
                return(property,null);
            }
            var field=parent.Fields.FirstOrDefault(f=>f.Owner==k&&f.Name==name&&!f.Hidden&&f.IsPublic);
            if(field is not null)return(null,field);
            if(parent.StaticFields.Any(f=>f.Name==name&&!f.Hidden&&f.IsPublic)||parent.Methods.Any(m=>m.Name==name&&m.IsPublic&&!m.IsAccessor))
                throw DeclarationError("AI3106","An inherited positional member must be a readable instance field or property.",at);
        }
        return(null,null);
    }
    private static void ValidateInheritedRecordParameters()
    {
        if((activeFeatures&ScalarFeatures.RecordInheritance)==0)return;
        foreach(var owner in activeStructs!.ToArray())
        {
            if(!owner.IsRecord||owner.BaseIndex<0||owner.IsTypeParameter)continue;
            for(var i=0;i<owner.RecordParameters.Count;i++)
            {
                var name=IdentifierName(owner.RecordParameters[i].Name);if(owner.CustomRecordMembers.Contains(name))continue;
                var at=owner.RecordParameterLocations[i];var (property,field)=ReadableInheritedRecordMember(owner.BaseIndex,name,at);
                if((property?.Type??field?.Type) is { } type&&type!=owner.RecordParameterTypes[i])throw DeclarationError("AI3106","An inherited positional member must have the same type as its primary parameter.",at);
            }
        }
    }
    private static List<ScalarStructField> BindInheritedRecordStorage(int index,ScalarStruct owner,List<ScalarStructField> own,IReadOnlyList<ScalarStructField> inherited)
    {
        var reused=owner.InheritedRecordParameters.ToHashSet(StringComparer.Ordinal);
        var properties=owner.Properties.ToList();
        foreach(var field in own.ToArray())
        {
            if(!field.IsProperty||field.Hidden||owner.CustomRecordMembers.Contains(field.Name)||!owner.RecordParameters.Any(p=>IdentifierName(p.Name)==field.Name))continue;
            var (property,existing)=ReadableInheritedRecordMember(owner.BaseIndex,field.Name,field.Location);
            if(property is null&&existing is null)continue;
            var type=property?.Type??existing!.Type;
            if(type!=field.Type)throw DeclarationError("AI3106","An inherited positional member must have the same type as its primary parameter.",field.Location);
            if(property?.IsAbstract==true)
            {
                if(!property.HasSet||!property.IsInit||property.SetPrivate)throw DeclarationError("AI3106","An inherited abstract positional property requires public get and init accessors.",field.Location);
                if(property.IsRequired)throw DeclarationError("AI3106","An override of a required member must be explicitly declared required.",field.Location);
                if(!properties.Any(p=>IdentifierName(p.Name)==field.Name&&p.IsRecordParameterOverride))
                {
                    var text=ParameterPassing(owner.RecordParameters.Single(p=>IdentifierName(p.Name)==field.Name).Type).Element;
                    properties.Add(new(field.Name,field.Type,text,true,true,owner.Name+".get_"+field.Name,true,owner.Name+".set_"+field.Name,false,field.Name,field.Location)
                    {Owner=index,IsAutomatic=true,IsOverride=true,IsInit=true,IsRequired=property.IsRequired,IsRecordParameterOverride=true});
                }
                continue;
            }
            own.Remove(field);reused.Add(field.Name);
        }
        activeStructs![index]=activeStructs[index] with {InheritedRecordParameters=reused,Properties=properties};
        return own;
    }
    private static void EnsureRecordPositionalOverrides()
    {
        foreach(var owner in activeStructs!.ToArray())
        {
            if(!owner.IsRecord)continue;var methods=owner.Methods.ToList();var index=IndexOf(owner.Id);
            foreach(var property in owner.Properties.Where(p=>p.IsRecordParameterOverride))
            {
                void Add(bool setter)
                {
                    var name=(setter?"set_":"get_")+property.Name;var target=owner.Name+"."+name;if(methods.Any(m=>m.QualifiedName==target))return;
                    var declaration=new FunctionDeclaration(name,setter?[new("value",property.TypeText)]:[],setter?"unit":property.TypeText,[],[],new FunctionBody([]){ScalarSource=new(setter?"field = value":"return field",property.Location)},[],property.Location);
                    methods.Add(new(name,target,true,false,declaration){Owner=index,IsOverride=true,IsAccessor=true,IsInitAccessor=setter,AccessorMember=(setter?"set:":"get:")+property.Name,Backing=property.Backing});
                }
                Add(false);Add(true);
            }
            activeStructs[index]=owner with{Methods=methods};
        }
    }
    private sealed partial class ExtendedBinder
    {
        private readonly bool _recordFieldsEquality;
        private List<ScalarStatement> BindRecordFieldsEquality()
        {
            var owner=_struct!;var at=owner.Location;var left=LocalNamed("__record_left")!;var right=LocalNamed("__record_right")!;
            if(!_equalityStack.Add(owner.Id))Fail("AI3100",$"Equality of the recursive record '{owner.Name}' is not supported yet.",at);
            try
            {
                var a=new ScalarExpression("local",owner.Id,at,left.EmittedName);var b=new ScalarExpression("local",owner.Id,at,right.EmittedName);ScalarExpression equal;
                if(CustomRecordEquality(owner)is { } custom)equal=new("call",ScalarType.Bool,at,custom.QualifiedName){Arguments=[a,b]};
                else
                {
                    equal=FieldsEqual(owner,a,b,at,ownOnly:owner.BaseIndex>=0);
                    if(owner.BaseIndex>=0)
                    {
                        var parent=_structs[owner.BaseIndex];var baseEqual=new ScalarExpression("call",ScalarType.Bool,at,parent.Name+"."+RecordEqualityMethodName(parent)){Arguments=[Upcast(a,parent.Id),Upcast(b,parent.Id)]};
                        equal=And(baseEqual,equal,at);
                    }
                    else
                    {
                        var notNull=new ScalarExpression("!=",ScalarType.Bool,at,Left:b,Right:new("null",owner.Id,at));
                        var contract=RecordContractValue(owner,a,at) is {} first?new ScalarExpression("==",ScalarType.Bool,at,Left:first,Right:RecordContractValue(owner,b,at)!):new ScalarExpression("record-contract-equal",ScalarType.Bool,at,Left:a,Right:b);
                        equal=And(And(notNull,contract,at),equal,at);
                    }
                    var same=new ScalarExpression("==",ScalarType.Bool,at,Left:a,Right:b);
                    equal=new("||",ScalarType.Bool,at,Left:same,Right:equal);
                }
                return [new("return",at,equal)];
            }
            finally{_equalityStack.Remove(owner.Id);}
        }
        private ScalarExpression? TryRecordClassBinary(string symbol,ScalarExpression left,ScalarExpression right,SourceLocation at)
        {
            ScalarType? Record(ScalarType type)
            {
                if(!IsStruct(type)||IsObject(type))return null;
                for(var k=IndexOf(type);k>=0;k=_structs[k].BaseIndex)if(_structs[k] is {IsRecord:true,IsClass:true})return _structs[k].Id;
                return null;
            }
            var first=Record(left.Type);var second=Record(right.Type);var site=DeconstructBindingSite(at);
            if(IsObject(left.Type)||IsObject(right.Type)||first is null&&second is null)
            {
                if(_checkOnly&&(IsReference(left.Type)||IsReference(right.Type)||IsNull(left.Type)||IsNull(right.Type)))checkedRecordEqualityTargets![site]="";
                return null;
            }
            if(first is null&&!IsNull(left.Type)||second is null&&!IsNull(right.Type))return null;
            ScalarType target;
            if(!_checkOnly&&(_typeArgs is not null||_struct?.TypeArguments is not null)&&checkedRecordEqualityTargets!.TryGetValue(site,out var signature)&&signature!="")target=Type(signature,at);
            else if(IsNull(left.Type))target=second!.Value;
            else if(IsNull(right.Type))target=first!.Value;
            else
            {
                var k=IndexOf(first!.Value);while(k>=0&&!Derives(_structs,IndexOf(second!.Value),k))k=_structs[k].BaseIndex;
                if(k<0){Fail("AI3107","Record equality requires a common record base type.",at);return null;}
                target=_structs[k].Id;
            }
            if(_checkOnly)checkedRecordEqualityTargets![site]=Show(target);
            return RecordEquality(symbol,Coerce(left,target),Coerce(right,target),at);
        }
        private ScalarExpression? DeclaredReferenceEquality(string symbol,ScalarExpression left,ScalarExpression right,SourceLocation at)
            =>!_checkOnly&&(_typeArgs is not null||_struct?.TypeArguments is not null)&&checkedRecordEqualityTargets!.TryGetValue(DeconstructBindingSite(at),out var target)&&target==""?ReferenceBinary(symbol,left,right,at):null;
        private ScalarSource ReadRecordBaseArguments()
        {
            Expect("(");var start=Current.Location;var depth=1;
            while(Current.Text!="")
            {
                if(Current.Text=="(")depth++;
                else if(Current.Text==")"&&--depth==0)
                {
                    var end=Current.Location;Next();return new(_declaration![(start.Offset-_declarationStart.Offset)..(end.Offset-_declarationStart.Offset)],start);
                }
                Next();
            }
            Fail("AI3106","Expected ')' after record base constructor arguments.",start);return null!;
        }
        private void BindRecordBaseInitializer(ScalarStruct owner,ScalarExpression self,List<ScalarStatement> result)
        {
            var parent=_structs[owner.BaseIndex];var source=owner.RecordBaseArguments;var at=owner.RecordBaseLocation??owner.Location;var arguments=new List<ScalarExpression>();var saved=(_tokens,_position);
            try
            {
                if(source is not null)
                {
                    _tokens=Lex(source.Text,source.Location,Has(ScalarFeatures.Floats)||Has(ScalarFeatures.Decimals),Has(ScalarFeatures.Chars),Has(ScalarFeatures.Strings));_position=0;NewLines();
                    if(Current.Text!="")do{arguments.Add(CallArgument());NewLines();}while(Take(","));
                    if(Current.Text!="")Fail("AI3106","Unexpected token in record base constructor arguments.",Current.Location);
                }
                if(Has(ScalarFeatures.RecordConstructors))
                {
                    var target=SelectRecordConstructor(parent,arguments,at,true);
                    var constructorCall=RecordConstructorCall(parent,target,self,arguments,at);
                    result.AddRange(_pendingDeclarations);_pendingDeclarations.Clear();result.Add(new("expression",at,constructorCall));return;
                }
                if(arguments.Count!=parent.RecordParameters.Count)Fail("AI3116",$"'{parent.Name}' has no constructor taking {arguments.Count} argument(s).",at);
                for(var i=0;i<arguments.Count;i++)arguments[i]=BindArgument(arguments[i],parent.RecordParameterTypes[i],ParameterPassing(parent.RecordParameters[i].Type).Mode);
                CompleteArguments(arguments);result.AddRange(_pendingDeclarations);_pendingDeclarations.Clear();
                var call=new ScalarExpression("call",parent.Id,at,parent.Name+"."+RecordInitializerName(parent)){Arguments=[Upcast(self,parent.Id),..arguments]};
                result.Add(new("expression",at,call));
            }
            finally{(_tokens,_position)=saved;}
        }
    }
}
