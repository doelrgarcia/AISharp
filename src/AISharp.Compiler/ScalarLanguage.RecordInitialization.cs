using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private static bool NeedsRecordConstruction(ScalarStruct owner) => owner.IsRecord && (owner.IsClass&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0 || owner.CustomRecordMembers.Count > 0 || owner.InstanceInitializers.Count > 0 || RecordConstructorsEnabled(owner) || owner.RecordParameters.Any(p => ParameterPassing(p.Type).Mode != ScalarParameterMode.Value));
    private static string RecordInitializerName(ScalarStruct owner) => "$record_initialize_" + StaticPropertyDeclaration(owner).ToString(CultureInfo.InvariantCulture);
    private static string RecordFactoryName(ScalarStruct owner) => owner.Name + ".$record_construct_" + StaticPropertyDeclaration(owner).ToString(CultureInfo.InvariantCulture);
    public static bool IsRecordFactory(ScalarStruct owner,string name)=>owner.IsRecord&&name==RecordFactoryName(owner);
    public static IReadOnlyList<ScalarParameterMode> RecordFactoryParameterModes(ScalarStruct owner)
        =>owner.RecordParameters.Select(p=>ParameterPassing(p.Type).Mode).ToArray();
    private static ScalarStructMethod RecordInitializerMethod(ScalarStruct owner,int index)
    {
        var parameters=new List<ParameterDeclaration>{new("$record_self",owner.Name)};parameters.AddRange(owner.RecordParameters);
        var declaration=new FunctionDeclaration("__record_initialize",parameters,owner.Name,[],[],new FunctionBody([]){ScalarSource=new("",owner.Location)},[],owner.Location);
        return new(RecordInitializerName(owner),owner.Name+"."+RecordInitializerName(owner),false,false,declaration){Owner=index,IsStatic=true,IsAccessor=true};
    }
    private sealed partial class ExtendedBinder
    {
        private readonly bool _recordInitialization;
        private Local? _recordSelf;
        private List<ScalarStatement> BindRecordInitializers()
        {
            var owner=_struct!;var result=new List<ScalarStatement>();var at=owner.Location;
            var self=new ScalarExpression("local",owner.Id,at,_recordSelf!.EmittedName);
            for(var i=0;i<owner.Positional;i++)
            {
                var name=IdentifierName(owner.RecordParameters[i].Name);if(owner.CustomRecordMembers.Contains(name)||owner.InheritedRecordParameters.Contains(name))continue;
                var index=owner.Fields.ToList().FindIndex(f=>f.Name==name&&f.Owner==IndexOf(owner.Id)&&f.IsProperty&&!f.Hidden);
                if(index<0)Fail("AI3106","Synthesized positional record storage is missing.",owner.Location);
                var parameter=LocalNamed(name)!;
                Store(index,new("local",parameter.Type,owner.Fields[index].Location,parameter.EmittedName),owner.Fields[index].Location);
            }
            var saved=(_tokens,_position);
            try
            {
                foreach(var initializer in owner.InstanceInitializers)
                {
                    var assigned=new HashSet<string>(_assigned);var patterns=new HashSet<string>(_patternLocals);
                    _scopes.Add(new(StringComparer.Ordinal));
                    try
                    {
                    var source=initializer.Expression;
                    _tokens=Lex(source.Text,source.Location,Has(ScalarFeatures.Floats)||Has(ScalarFeatures.Decimals),Has(ScalarFeatures.Chars),Has(ScalarFeatures.Strings));_position=0;
                    var expression=Expression();Read(expression);NewLines();
                    if(Current.Text!="")Fail("AI3106","Unexpected token in record initializer.");
                    var index=owner.Fields.ToList().FindIndex(f=>f.Name==initializer.Field&&f.Owner==IndexOf(owner.Id));
                    if(index<0)Fail("AI3106","Record initializer storage does not belong to its declaring type.",source.Location);
                    expression=Coerce(expression,owner.Fields[index].Type);
                    result.AddRange(_pendingDeclarations);_pendingDeclarations.Clear();
                    Store(index,expression,source.Location);
                    }
                    finally
                    {
                        _scopes.RemoveAt(_scopes.Count-1);_assigned=assigned;
                        _patternLocals.Clear();foreach(var name in patterns)_patternLocals.Add(name);
                    }
                }
            }
            finally { (_tokens,_position)=saved; }
            if(owner.BaseIndex>=0&&Has(ScalarFeatures.RecordInheritance))BindRecordBaseInitializer(owner,self,result);
            result.Add(new("return",at,self));return result;
            void Store(int index,ScalarExpression value,SourceLocation location)
            {
                var target=new ScalarExpression("field",owner.Fields[index].Type,location,index.ToString(CultureInfo.InvariantCulture),Left:self);
                result.Add(new("expression",location,new("=",target.Type,location,Left:target,Right:value)));
            }
        }
        private void LiftRecordFactory(IReadOnlyList<ScalarParameter> parameters)
        {
            var owner=_struct!;var at=owner.Location;var receiver=parameters[0];var arguments=parameters.Skip(1).ToArray();
            if(owner.IsAbstract)return;
            var self=new ScalarExpression("local",owner.Id,at,receiver.Name);
            var invoke=new ScalarExpression("call",owner.Id,at,owner.Name+"."+RecordInitializerName(owner))
                {Arguments=[self,..arguments.Select(p=>p.Mode==ScalarParameterMode.Value?new ScalarExpression("local",p.Type,at,p.Name):new("argument-in",p.Type,at,Left:new("local",p.Type,at,p.Name)))]};
            ScalarExpression allocation=new("new",owner.Id,at);
            if(!owner.IsClass&&owner.Methods.Any(m=>m.IsStaticConstructor&&!m.IsImplicitStaticConstructor))
                allocation=new("type-init-value",owner.Id,at,IndexOf(owner.Id).ToString(CultureInfo.InvariantCulture),Left:allocation);
            Lift(new ScalarFunction(RecordFactoryName(owner),arguments,owner.Id,
                [new ScalarStatement("local",at,allocation,receiver.Name,owner.Id){Mutable=!owner.IsClass},new("return",at,invoke)],
                [TrueContract(at)],at));
        }
        private ScalarExpression NewInitializedRecord(ScalarStruct owner,Lexeme token,SourceLocation at)
        {
            if(owner.IsAbstract||owner.IsInterface)Fail("AI3116",$"Cannot create an instance of the abstract type '{owner.Name}'.",token.Location);
            var arguments=new List<ScalarExpression>();
            if(Take("("))
            {
                if(!Take(")")){do{arguments.Add(CallArgument());}while(Take(","));Expect(")");}
            }
            else if(Current.Text!="{")Fail("AI3106","Expected '(' or '{' after the record type.");
            ScalarExpression value;
            if(!owner.IsClass&&owner.Positional>0&&arguments.Count==0)value=new("new",owner.Id,at);
            else
            {
                if(arguments.Count!=owner.RecordParameters.Count)Fail("AI3116",$"'{owner.Name}' has no constructor taking {arguments.Count} argument(s).",token.Location);
                for(var i=0;i<arguments.Count;i++)arguments[i]=BindArgument(arguments[i],owner.RecordParameterTypes[i],ParameterPassing(owner.RecordParameters[i].Type).Mode);
                CompleteArguments(arguments);
                value=new("call",owner.Id,at,RecordFactoryName(owner)){Arguments=arguments};
            }
            return RequiredObjectInitializer(owner,value,token.Location,enforceRequired:Has(ScalarFeatures.RequiredMembers));
        }
    }
}
