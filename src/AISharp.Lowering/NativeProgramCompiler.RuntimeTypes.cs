using AISharp.Compiler;
using System.Globalization;
using System.Text;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    [ThreadStatic] private static int typePointerBits;
    private sealed record RuntimeTypeValue(ScalarType Result,ScalarType Represented,Value Operand,IReadOnlyList<int> RuntimeClasses,SourceLocation At):Value(At){public override ScalarType Type=>Result;}
    private sealed record NativeTypeHash(Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.Int;}
    private sealed partial class Verifier
    {
        private ScalarStruct RequireNativeTypeDescriptor(ScalarType result,SourceLocation at)
        {
            var all=layouts??throw new Rejected(at,"Native type metadata is unavailable.");
            var bases=all.Where(s=>s.IsNativeType).ToArray();var concrete=all.Where(s=>s.IsNativeRuntimeType).ToArray();
            if(bases.Length!=1||concrete.Length!=1||bases[0].Id!=result||bases[0].Name!=ScalarLanguage.NativeTypeClassName||!bases[0].IsClass||!bases[0].IsAbstract||bases[0].Fields.Count!=0
                ||concrete[0].Name!=ScalarLanguage.NativeRuntimeTypeClassName||!concrete[0].IsClass||!concrete[0].IsSealed||concrete[0].BaseIndex!=(int)result-ScalarLanguage.StructTypeBase||concrete[0].Fields.Count!=1
                ||concrete[0].Fields[0] is not {Name:ScalarLanguage.NativeTypeIdentityField,Type:ScalarType.Int,StorageType:ScalarType.Int,IsPublic:false,IsReadonly:true})
                throw new Rejected(at,"A native type operation requires the compiler-owned immutable Type descriptor hierarchy.");
            if(ScalarLanguage.ConcreteOf(all,(int)result-ScalarLanguage.StructTypeBase).Any(i=>!all[i].IsNativeRuntimeType))
                throw new Rejected(at,"A source subtype cannot replace the native runtime Type descriptor.");
            return concrete[0];
        }
        private Value VerifyRuntimeType(ScalarExpression expression)
        {
            if(expression is not {Left:not null,Right:null,Arguments:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null}
                ||!int.TryParse(expression.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var identity)||!KnownRuntimeType((ScalarType)identity))
                throw new Rejected(expression.Location,"GetType requires one verified value and its logical source type.");
            RequireNativeTypeDescriptor(expression.Type,expression.Location);var represented=(ScalarType)identity;
            if(represented==ScalarType.Unit)throw new Rejected(expression.Location,"A void value has no runtime type receiver.");
            var storage=ScalarConstants.IsNative(represented)?ScalarConstants.NativeStorage(represented,typePointerBits is 32?32:64):typeEnums?.FirstOrDefault(e=>e.Id==represented)?.Underlying??represented;
            if(expression.Left.Type!=storage)throw new Rejected(expression.Location,"The runtime type receiver does not match its verified source type and target storage.");
            var operand=Require(VerifyValue(expression.Left,expression.Location),storage,expression.Location);var runtime=new List<int>();
            if(IsReferenceType(storage))
                for(var i=0;i<layouts!.Count;i++)
                {
                    var owner=layouts[i];if(!owner.IsClass||owner.IsInterface||owner.IsAbstract||owner.IsTypeParameter||owner.IsCheckInstance)continue;
                    if(IsObjectType(storage)||ScalarLanguage.Derives(layouts,i,(int)storage-ScalarLanguage.StructTypeBase))runtime.Add(i);
                }
            return new RuntimeTypeValue(expression.Type,represented,operand,runtime,expression.Location);
        }
        private Value VerifyNativeTypeHash(ScalarExpression expression)
        {
            if(expression is not {Type:ScalarType.Int,Left:not null,Right:null,Arguments:null,Value:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null})
                throw new Rejected(expression.Location,"A Type hash requires one verified Type reference and an Int32 result.");
            RequireNativeTypeDescriptor(expression.Left.Type,expression.Location);
            return new NativeTypeHash(Require(VerifyValue(expression.Left,expression.Location),expression.Left.Type,expression.Location),expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private string EmitRuntimeType(RuntimeTypeValue value)
        {
            var receiver=Atom(value.Operand);
            if(IsReferenceType(value.Operand.Type))
            {
                var name="ai_runtime_type_"+((int)value.Operand.Type).ToString(CultureInfo.InvariantCulture);var result=CType(value.Type);
                if(!helpers.ContainsKey(name))
                {
                    var text=new StringBuilder("static "+result+name+"(const void *value) { if (value == NULL) ai_fault(\"System.NullReferenceException\"); int32_t tag; memcpy(&tag,value,sizeof tag); switch(tag) {\n");
                    foreach(var index in value.RuntimeClasses)
                    {
                        var identity=layouts![index].IsStringObject?(int)ScalarType.String:layouts[index].IsObjectRoot?ScalarLanguage.ObjectTypeId:(int)(layouts[index].BoxedType??layouts[index].Id);typeLiterals.Add(identity);text.Append("case ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(": return (").Append(result).Append(")&").Append(TypeLiteralName(identity)).AppendLine(";");
                    }
                    text.Append("default: ai_fault(\"System.InvalidCastException\"); return NULL; } }\n");Helper(name,text.ToString());
                }
                return name+"("+receiver+")";
            }
            if(value.Operand.Type==ScalarType.String)c.Append("if ((").Append(receiver).AppendLine(").p == NULL) ai_fault(\"System.NullReferenceException\");");
            else c.Append("(void)(").Append(receiver).AppendLine(");");
            return EmitTypeLiteral(new(value.Type,value.Represented,value.Location));
        }
        private string EmitNativeTypeHash(NativeTypeHash value)
        {
            HashBits();HashSip();var all=layouts!;var descriptor=all.Single(s=>s.IsNativeRuntimeType);var index=(int)descriptor.Id-ScalarLanguage.StructTypeBase;
            var nominal="identity >= INT32_C("+ScalarLanguage.StructTypeBase.ToString(CultureInfo.InvariantCulture)+") && identity < INT32_C("+(ScalarLanguage.StructTypeBase+all.Count).ToString(CultureInfo.InvariantCulture)+")";
            var text="static int32_t ai_hash_native_type(const void *value) { if(value == NULL) ai_fault(\"System.NullReferenceException\"); int32_t tag; memcpy(&tag,value,sizeof tag); if(tag != INT32_C("+index.ToString(CultureInfo.InvariantCulture)+")) ai_fault(\"System.InvalidCastException\"); int32_t identity=((const ai_struct_"+index.ToString(CultureInfo.InvariantCulture)+" *)value)->f0; identity=("+nominal+") ? identity - INT32_C("+ScalarLanguage.StructTypeBase.ToString(CultureInfo.InvariantCulture)+") : -identity - INT32_C(1); uint64_t h=ai_hash_bytes((const uint8_t *)&identity,sizeof identity); return ai_hash_00_bits((uint32_t)h ^ (uint32_t)(h >> 32)); }\n";
            return Helper("ai_hash_native_type",text)+"("+Atom(value.Operand)+")";
        }
    }
}
