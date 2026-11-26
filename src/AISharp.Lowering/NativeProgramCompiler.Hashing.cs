using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record ScalarHash(Value Operand,SourceLocation At):Value(At)
    {
        public override ScalarType Type=>ScalarType.Int;
    }
    private sealed record HashContract(Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.Int;}
    private sealed record IdentityHash(Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.Int;}
    private sealed partial class Verifier
    {
        private Value VerifyScalarHash(ScalarExpression expression)
        {
            if(expression is not {Type:ScalarType.Int,Left:not null,Right:null,Arguments:null,Value:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null}
                || !(ScalarConstants.IsInteger(expression.Left.Type)&&!ScalarConstants.IsNative(expression.Left.Type)||ScalarConstants.IsFloat(expression.Left.Type)||expression.Left.Type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal or ScalarType.String))
                throw new Rejected(expression.Location,"A scalar hash requires one supported scalar operand and an Int32 result.");
            return new ScalarHash(Require(VerifyValue(expression.Left,expression.Location),expression.Left.Type,expression.Location),expression.Location);
        }
        private Value VerifyHashContract(ScalarExpression expression)
        {
            if(expression is not {Type:ScalarType.Int,Left:not null,Right:null,Arguments:null,Value:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null}||!IsClassType(expression.Left.Type)||!layouts![(int)expression.Left.Type-ScalarLanguage.StructTypeBase].IsRecord)
                throw new Rejected(expression.Location,"A record hash contract requires one verified record-class receiver and an Int32 result.");
            return new HashContract(Require(VerifyValue(expression.Left,expression.Location),expression.Left.Type,expression.Location),expression.Location);
        }
        private Value VerifyIdentityHash(ScalarExpression expression)
        {
            if(expression is not {Type:ScalarType.Int,Left:not null,Right:null,Arguments:null,Value:null,Wraps:false,AccessorSetter:null,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null}||!IsReferenceType(expression.Left.Type))
                throw new Rejected(expression.Location,"An identity hash requires one verified reference operand and an Int32 result.");
            return new IdentityHash(Require(VerifyValue(expression.Left,expression.Location),expression.Left.Type,expression.Location),expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private string HashBits()=>Helper("ai_hash_00_bits","static int32_t ai_hash_00_bits(uint32_t bits) { int32_t value; memcpy(&value, &bits, sizeof(value)); return value; }\n");
        private string EmitHashContract(HashContract hash)
        {
            HashBits();HashSip();
            return Helper("ai_hash_contract","static int32_t ai_hash_contract(const void *value) { if (value == NULL) ai_fault(\"System.NullReferenceException\"); int32_t tag; memcpy(&tag, value, sizeof(tag)); uint64_t h = ai_hash_bytes((const uint8_t *)&tag, sizeof(tag)); return ai_hash_00_bits((uint32_t)h ^ (uint32_t)(h >> 32)); }\n")+"("+Atom(hash.Operand)+")";
        }
        private string EmitIdentityHash(IdentityHash hash)
        {
            HashBits();HashSip();
            return Helper("ai_hash_identity","static int32_t ai_hash_identity(const void *value) { if (value == NULL) return INT32_C(0); uintptr_t identity = (uintptr_t)value; uint64_t h = ai_hash_bytes((const uint8_t *)&identity, sizeof(identity)); return ai_hash_00_bits((uint32_t)h ^ (uint32_t)(h >> 32)); }\n")+"("+Atom(hash.Operand)+")";
        }
        private string EmitScalarHash(ScalarHash hash)
        {
            var operand=Atom(hash.Operand);var type=hash.Operand.Type;var bits=HashBits();
            if(type==ScalarType.String)return EmitStringHash(operand);
            if(type==ScalarType.Bool)return "("+operand+" ? INT32_C(1) : INT32_C(0))";
            if(type==ScalarType.Char)return bits+"((uint32_t)("+operand+") * UINT32_C(65537))";
            if(type==ScalarType.F32)
                return Helper("ai_hash_f32","static int32_t ai_hash_f32(float value) { uint32_t bits; memcpy(&bits, &value, sizeof(bits)); if (value == 0 || value != value) bits &= UINT32_C(0x7f800000); return ai_hash_00_bits(bits); }\n")+"("+operand+")";
            if(type==ScalarType.F64)
                return Helper("ai_hash_f64","static int32_t ai_hash_f64(double value) { uint64_t bits; memcpy(&bits, &value, sizeof(bits)); if (value == 0 || value != value) bits &= UINT64_C(0x7ff0000000000000); return ai_hash_00_bits((uint32_t)bits ^ (uint32_t)(bits >> 32)); }\n")+"("+operand+")";
            if(type==ScalarType.Decimal)
            {
                var text="static int32_t ai_hash_decimal(ai_dec value) {\n"+
                    "ai_big mantissa = ai_big_of(value); if (ai_big_zero(&mantissa)) return INT32_C(0);\n"+
                    "int scale = ai_dec_scale_of(value); while (scale > 0) { ai_big divided = mantissa; if (ai_big_div_small(&divided, 10u) != 0u) break; mantissa = divided; --scale; }\n"+
                    "uint32_t flags = (value.flags & AI_DEC_SIGN) | ((uint32_t)scale << 16);\n"+
                    "return ai_hash_00_bits(flags ^ mantissa.w[0] ^ mantissa.w[1] ^ mantissa.w[2]);\n}\n";
                return Helper("ai_hash_decimal",text)+"("+operand+")";
            }
            if(ScalarConstants.Bits(type)<=32)return bits+"((uint32_t)("+operand+"))";
            return Helper("ai_hash_integer64","static int32_t ai_hash_integer64(uint64_t value) { return ai_hash_00_bits((uint32_t)value ^ (uint32_t)(value >> 32)); }\n")+"((uint64_t)("+operand+"))";
        }
    }
}
