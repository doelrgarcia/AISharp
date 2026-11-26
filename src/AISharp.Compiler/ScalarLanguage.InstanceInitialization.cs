using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    [ThreadStatic] private static HashSet<(int Owner, string Backing)>? instancePropertyFieldUses;

    private static string InstanceInitializerName(ScalarStruct owner,int ordinal) => "$instance_value_" + StaticPropertyDeclaration(owner).ToString(CultureInfo.InvariantCulture) + "_" + ordinal.ToString(CultureInfo.InvariantCulture);
    private static ScalarStructMethod InstanceInitializerMethod(ScalarStruct owner, int index, int ordinal)
    {
        var initializer = owner.InstanceInitializers[ordinal];var expression = initializer.Expression;
        var at = new SourceLocation(expression.Location.Line,expression.Location.Column-7,expression.Location.Offset-7);
        var source = new ScalarSource("return " + expression.Text,at);
        accessorPrefixes![source] = expression.Location;
        var declaration = new FunctionDeclaration("__instance_value_" + ordinal.ToString(CultureInfo.InvariantCulture),[],initializer.TypeText,[],[],new FunctionBody([]){ScalarSource=source},[],expression.Location);
        return new(InstanceInitializerName(owner,ordinal),owner.Name+"."+InstanceInitializerName(owner,ordinal),false,false,declaration){Owner=index,IsStatic=true,IsAccessor=true};
    }
    private static void ValidateInstancePropertyInitializers()
    {
        foreach(var owner in activeStructs!)foreach(var initializer in owner.InstanceInitializers)
        {
            var property=owner.Properties.FirstOrDefault(p=>!p.IsStatic&&p.Backing==initializer.Field);
            if(property is null)continue;
            if(property.IsAutomatic || property.HasGet&&property.Getter is null || property.HasSet&&property.Setter is null
                || instancePropertyFieldUses!.Contains(StaticPropertyKey(owner,initializer.Field)))continue;
            throw DeclarationError("AI3106","Only a field-backed property can have an initializer.",property.Location);
        }
    }
    private sealed partial class ExtendedBinder
    {
        private void AddInstanceInitializer(ScalarStruct owner,List<ScalarInstanceInitializer> initializers,string field,string type,SourceLocation at)
        {
            if(!Has(ScalarFeatures.InstanceInitializers))Fail("AI3100","Instance initializers are disabled in this profile.",at);
            if(owner.IsInterface)Fail("AI3106","An interface cannot have instance storage initializers.",at);
            if(owner.IsRecord&&!Has(ScalarFeatures.RecordInitializers))Fail("AI3100","Record member initializers are disabled in this profile.",at);
            initializers.Add(new(field,type,ReadStaticInitializer()));
        }
        private IReadOnlyList<ScalarStatement> BindInstanceInitializers()
        {
            var owner=_struct!;var result=new List<ScalarStatement>();
            for(var ordinal=0;ordinal<owner.InstanceInitializers.Count;ordinal++)
            {
                var initializer=owner.InstanceInitializers[ordinal];var at=initializer.Expression.Location;
                var field=owner.Fields.ToList().FindIndex(f=>f.Name==initializer.Field&&f.Owner==IndexOf(owner.Id));
                if(field<0)Fail("AI3106","Instance initializer storage does not belong to its declaring type.",at);
                var self=new ScalarExpression("local",owner.Id,at,_this!.EmittedName);
                var target=new ScalarExpression("field",owner.Fields[field].Type,at,field.ToString(CultureInfo.InvariantCulture),Left:self);
                var call=new ScalarExpression("call",target.Type,at,owner.Name+"."+InstanceInitializerName(owner,ordinal)){Arguments=[]};
                result.Add(new("expression",at,new("=",target.Type,at,Left:target,Right:call)));
            }
            return result;
        }
    }
}
