using AISharp.Compiler;
using System.Globalization;
using System.Text;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    [ThreadStatic] private static string? typeModule;
    private sealed record NativeTypeName(Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.String;}
    private sealed partial class Verifier
    {
        private Value VerifyNativeTypeName(ScalarExpression expression)
        {
            if(expression is not {Type:ScalarType.String,Left:not null,Right:null,Arguments:null,Value:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null})
                throw new Rejected(expression.Location,"Type formatting requires one verified Type reference and a string result.");
            RequireNativeTypeDescriptor(expression.Left.Type,expression.Location);
            return new NativeTypeName(Require(VerifyValue(expression.Left,expression.Location),expression.Left.Type,expression.Location),expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        internal static string NativeTypeDisplay(ScalarType type)
        {
            var scalar=type switch
            {
                ScalarType.Int=>"Int32",ScalarType.Bool=>"Boolean",ScalarType.Unit=>"Void",ScalarType.Long=>"Int64",ScalarType.UInt=>"UInt32",ScalarType.ULong=>"UInt64",ScalarType.Short=>"Int16",ScalarType.UShort=>"UInt16",ScalarType.Byte=>"Byte",ScalarType.SByte=>"SByte",ScalarType.F32=>"Single",ScalarType.F64=>"Double",ScalarType.Char=>"Char",ScalarType.String=>"String",ScalarType.Decimal=>"Decimal",ScalarType.NInt=>"IntPtr",ScalarType.NUInt=>"UIntPtr",_=>null
            };
            if(scalar is not null)return "System."+scalar;
            if(IsObjectType(type))return "System.Object";
            var prefix=string.IsNullOrEmpty(typeModule)?"":typeModule+".";
            if(typeEnums?.FirstOrDefault(e=>e.Id==type) is {} enumeration)
            {
                var enumName=enumeration.SourceName is {} en?(enumeration.SourceRuntimeFullName??enumeration.SourceFullName??enumeration.SourceNamespace+"."+en):prefix+enumeration.Name.Split('<')[0];
                if(enumeration.TypeParameters.Count==0)return enumName;
                return enumName+"["+string.Join(",",enumeration.TypeArguments.Count>0?enumeration.TypeArguments.Select(EnumArgumentDisplay):enumeration.SourceTypeParameterNames)+"]";
            }
            var owner=Layout(type);if(owner.IsNativeType)return "System.Type";if(owner.IsNativeRuntimeType)return "System.RuntimeType";if(owner.IsStringArray)return "System.String[]";
            if(owner.ArrayElementType is { } element)return NativeTypeDisplay(element)+"[]";
            if(owner.Name=="StringBuilder"&&owner.Fields.Any(f=>f.Name=="__record_display_text"))return "System.Text.StringBuilder";
            var name=owner.SourceName is {} sourceName?(owner.SourceRuntimeFullName??owner.SourceFullName??owner.SourceNamespace+"."+sourceName):prefix+owner.Name.Split('<')[0];
            if(owner.IsCheckInstance&&owner.Template==(int)owner.Id-ScalarLanguage.StructTypeBase&&owner.TypeParameters.Count>0)
                return name+((owner.SourceOwnArity??owner.TypeParameters.Count) is var own&&own>0?"`"+own.ToString(CultureInfo.InvariantCulture):"")+"["+string.Join(",",owner.SourceTypeParameterNames.Count==owner.TypeParameters.Count?owner.SourceTypeParameterNames:owner.TypeParameters)+"]";
            return owner.TypeArgumentList.Count==0?name:name+((owner.SourceOwnArity??owner.TypeArgumentList.Count) is var constructedArity&&constructedArity>0?"`"+constructedArity.ToString(CultureInfo.InvariantCulture):"")+"["+string.Join(",",owner.TypeArgumentList.Select(NativeTypeDisplay))+"]";
        }
        private static string EnumArgumentDisplay(ScalarRuntimeTypeShape shape)=>shape.Kind switch
        {
            ScalarRuntimeTypeKind.Named=>NativeTypeDisplay(shape.NamedType!.Value),
            ScalarRuntimeTypeKind.Array=>EnumArgumentDisplay(shape.Element!)+"["+new string(',',shape.Rank-1)+"]",
            ScalarRuntimeTypeKind.Nullable=>"System.Nullable`1["+EnumArgumentDisplay(shape.Element!)+"]",
            ScalarRuntimeTypeKind.ValueTuple=>"System.ValueTuple`"+shape.Arguments!.Count.ToString(CultureInfo.InvariantCulture)+"["+string.Join(",",shape.Arguments.Select(EnumArgumentDisplay))+"]",
            ScalarRuntimeTypeKind.ValueTupleDefinition when shape.Rank==0=>"System.ValueTuple",
            ScalarRuntimeTypeKind.Constructed=>NativeTypeDisplay(shape.NamedType!.Value).Split('[')[0]+"["+string.Join(",",shape.Arguments!.Select(EnumArgumentDisplay))+"]",
            _=>throw new Rejected(shape.Location,"An enum type argument has no closed runtime name.")
        };
        private bool usesNativeTypeNames;
        private void CompleteNativeTypeNames()
        {
            const string name="ai_native_type_name";
            if(!usesNativeTypeNames)return;
            if(!helpers.ContainsKey(name))
            {
                var all=layouts!;var descriptor=all.Single(s=>s.IsNativeRuntimeType);var index=(int)descriptor.Id-ScalarLanguage.StructTypeBase;
                var text=new StringBuilder("static ai_str "+name+"(const void *value) { if(value == NULL) ai_fault(\"System.NullReferenceException\"); int32_t tag; memcpy(&tag,value,sizeof tag); if(tag != INT32_C("+index.ToString(CultureInfo.InvariantCulture)+")) ai_fault(\"System.InvalidCastException\"); int32_t identity=((const ai_struct_"+index.ToString(CultureInfo.InvariantCulture)+" *)value)->f0; switch(identity) {\n");
                var identities=Enum.GetValues<ScalarType>().Concat([(ScalarType)ScalarLanguage.ObjectTypeId]).Concat(typeEnums?.Where(e=>KnownRuntimeType(e.Id)).Select(e=>e.Id)??[]).Concat(all.Where(s=>KnownRuntimeType(s.Id)).Select(s=>s.Id)).Distinct().OrderBy(t=>(int)t);
                foreach(var type in identities)
                {
                    var display=NativeTypeDisplay(type);text.Append("case ").Append(((int)type).ToString(CultureInfo.InvariantCulture)).Append(": { static const uint16_t text[] = { ").Append(string.Join(",",display.Select(c=>((int)c).ToString(CultureInfo.InvariantCulture)))).Append(",0 }; ai_str result = {text, ").Append(display.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("}; return result; }");
                }
                foreach(var entry in shapeLiterals.Values.OrderBy(t=>t.Id))
                {
                    var display=entry.Shape.Display;text.Append("case ").Append(entry.Id.ToString(CultureInfo.InvariantCulture)).Append(": { static const uint16_t text[] = { ").Append(string.Join(",",display.Select(c=>((int)c).ToString(CultureInfo.InvariantCulture)))).Append(",0 }; ai_str result = {text, ").Append(display.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("}; return result; }");
                }
                text.Append("default: ai_fault(\"System.InvalidCastException\"); ai_str missing={NULL,0}; return missing; } }\n");Helper(name,text.ToString());
            }
        }
        private string EmitNativeTypeName(NativeTypeName value)
        {
            usesNativeTypeNames=true;return "ai_native_type_name("+Atom(value.Operand)+")";
        }
    }
}
