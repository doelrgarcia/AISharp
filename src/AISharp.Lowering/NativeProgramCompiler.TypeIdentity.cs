using AISharp.Compiler;
using System.Globalization;
using System.Text;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    [ThreadStatic] private static IReadOnlyList<ScalarEnum>? typeEnums;
    private sealed record TypeLiteral(ScalarType Result,ScalarType Represented,SourceLocation At):Value(At){public override ScalarType Type=>Result;}
    private static bool KnownRuntimeType(ScalarType type)=>Enum.IsDefined(type)||IsObjectType(type)||typeEnums?.Any(e=>e.Id==type&&(!e.IsCheckInstance||e.Template==(int)e.Id-ScalarLanguage.EnumTypeBase))==true||IsStructType(type)&&!Layout(type).IsTypeParameter&&(!Layout(type).IsCheckInstance||Layout(type).Template==(int)type-ScalarLanguage.StructTypeBase&&Layout(type).TypeParameters.Count>0);
    private sealed partial class Verifier
    {
        private Value VerifyTypeLiteral(ScalarExpression expression)
        {
            if(expression is not {Left:null,Right:null,Arguments:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null}
                ||!IsClassType(expression.Type)||!Layout(expression.Type).IsNativeType
                ||!int.TryParse(expression.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var id)||!KnownRuntimeType((ScalarType)id))
                throw new Rejected(expression.Location,"A type literal requires one verified runtime type identity and the native Type reference result.");
            RequireNativeTypeDescriptor(expression.Type,expression.Location);
            return new TypeLiteral(expression.Type,(ScalarType)id,expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private readonly SortedSet<int> typeLiterals=[];
        private static string TypeLiteralName(int identity)=>"ai_type_literal_"+identity.ToString(CultureInfo.InvariantCulture);
        private string EmitTypeLiteral(TypeLiteral literal)
        {
            typeLiterals.Add((int)literal.Represented);return "("+CType(literal.Type)+")&"+TypeLiteralName((int)literal.Represented);
        }
        private string TypeLiteralDeclarations()
        {
            CompleteNativeTypeNames();
            if(typeLiterals.Count==0)return "";
            var descriptor=layouts!.Single(s=>s.IsNativeRuntimeType);var index=(int)descriptor.Id-ScalarLanguage.StructTypeBase;var text=new StringBuilder();
            foreach(var identity in typeLiterals)text.Append("static ai_struct_").Append(index.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(TypeLiteralName(identity)).Append(" = { INT32_C(").Append(index.ToString(CultureInfo.InvariantCulture)).Append("), INT32_C(").Append(identity.ToString(CultureInfo.InvariantCulture)).AppendLine(") }; ");
            return text.ToString();
        }
    }
}
