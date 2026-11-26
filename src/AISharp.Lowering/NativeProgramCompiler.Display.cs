using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record DisplayValue(Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.String;}
    private sealed partial class Verifier
    {
        private Value VerifyDisplayValue(ScalarExpression expression)
        {
            if(expression is not {Type:ScalarType.String,Left:not null,Right:null,Arguments:null,Value:null}||!(ScalarConstants.IsInteger(expression.Left.Type)||ScalarConstants.IsFloat(expression.Left.Type)||expression.Left.Type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal))
                throw new Rejected(expression.Location,"Display conversion requires one supported scalar operand and a string result.");
            var value=Require(VerifyValue(expression.Left,expression.Location),expression.Left.Type,expression.Location);return new DisplayValue(value,expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private bool usesDisplay;
        private string EmitDisplayValue(DisplayValue display)
        {
            usesDisplay=true;var value=Atom(display.Operand);var type=display.Operand.Type;
            if(type==ScalarType.Bool)
            {
                var yes=Atom(new SConst("True",display.At));var no=Atom(new SConst("False",display.At));return "("+value+" ? "+yes+" : "+no+")";
            }
            if(type==ScalarType.Char)
                return Helper("ai_display_char","static ai_str ai_display_char(uint16_t value) { uint16_t *p = (uint16_t *)ai_arena_alloc(sizeof(uint16_t)); *p = value; ai_str s = { p, 1 }; return s; }\n")+"("+value+")";
            if(type==ScalarType.Decimal)
            {
                DisplayAscii();return Helper("ai_display_decimal","static ai_str ai_display_decimal(ai_dec value) { char text[48]; ai_dec_format(value, text); return ai_display_ascii(text); }\n")+"("+value+")";
            }
            if(ScalarConstants.IsFloat(type))return DisplayFloat(type)+"("+value+")";
            var negative=ScalarConstants.IsSigned(type)?"value < 0":"0";var ct=ScalarConstants.IsSigned(type)?"int64_t":"uint64_t";var suffix=ScalarConstants.IsSigned(type)?"signed":"unsigned";
            var magnitude=ScalarConstants.IsSigned(type)?"negative ? (uint64_t)(-(value + 1)) + UINT64_C(1) : (uint64_t)value":"value";
            var name="ai_display_"+suffix;
            var text="static ai_str "+name+"("+ct+" value) {\nuint16_t digits[21]; int32_t n = 0; int negative = "+negative+"; uint64_t v = "+magnitude+";\ndo { digits[n++] = (uint16_t)('0' + (v % UINT64_C(10))); v /= UINT64_C(10); } while (v != 0);\nif (negative) digits[n++] = (uint16_t)'-';\nuint16_t *p = (uint16_t *)ai_arena_alloc((size_t)n * sizeof(uint16_t)); for (int32_t i = 0; i < n; ++i) p[i] = digits[n - 1 - i]; ai_str s = { p, n }; return s;\n}\n";
            return Helper(name,text)+"(("+ct+")("+value+"))";
        }
        private void DisplayAscii()=>Helper("ai_display_ascii","static ai_str ai_display_ascii(const char *text) { size_t n = strlen(text); uint16_t *p = (uint16_t *)ai_arena_alloc(n * sizeof(uint16_t)); for (size_t i = 0; i < n; ++i) p[i] = (uint16_t)(unsigned char)text[i]; ai_str s = { p, (int32_t)n }; return s; }\n");
        private string DisplayFloat(ScalarType type)
        {
            DisplayAscii();var single=type==ScalarType.F32;var limit=single?9:17;var ctype=single?"float":"double";var parse=single?"strtof":"strtod";var name=single?"ai_display_f32":"ai_display_f64";
            var text="static ai_str "+name+"("+ctype+" value) {\n"+
                "if (isnan(value)) return ai_display_ascii(\"NaN\"); if (isinf(value)) return ai_display_ascii(value < 0 ? \"-Infinity\" : \"Infinity\");\n"+
                "if (value == 0) return ai_display_ascii(signbit(value) ? \"-0\" : \"0\");\n"+
                "char raw[64], digits[32], result[384]; int precision, n = 0, exponent = 0, before = 0, after = 0, p = 0;\n"+
                "for (precision = 1; precision <= "+limit+"; ++precision) { snprintf(raw, sizeof raw, \"%.*g\", precision, (double)value); if (("+ctype+")"+parse+"(raw, NULL) == value) break; }\n"+
                "const char *s = raw; if (*s == '-') { result[p++] = '-'; ++s; }\n"+
                "for (; *s != 0 && *s != 'e' && *s != 'E'; ++s) { if (*s == '.') { after = 1; continue; } digits[n++] = *s; if (!after) ++before; }\n"+
                "if (*s != 0) exponent = atoi(s + 1); exponent += before - 1;\n"+
                "int start = 0; while (start < n - 1 && digits[start] == '0') { ++start; --exponent; } while (n > start + 1 && digits[n - 1] == '0') --n;\n"+
                "if (exponent < -4 || exponent >= "+limit+") { result[p++] = digits[start++]; if (start < n) { result[p++] = '.'; while (start < n) result[p++] = digits[start++]; } result[p++] = 'E'; result[p++] = exponent < 0 ? '-' : '+'; int e = exponent < 0 ? -exponent : exponent; if (e >= 100) result[p++] = (char)('0' + e / 100); result[p++] = (char)('0' + e / 10 % 10); result[p++] = (char)('0' + e % 10); }\n"+
                "else if (exponent < 0) { result[p++] = '0'; result[p++] = '.'; for (int i = -1; i > exponent; --i) result[p++] = '0'; while (start < n) result[p++] = digits[start++]; }\n"+
                "else { int i = 0; for (; i <= exponent; ++i) result[p++] = start < n ? digits[start++] : '0'; if (start < n) { result[p++] = '.'; while (start < n) result[p++] = digits[start++]; } }\n"+
                "result[p] = 0; return ai_display_ascii(result);\n}\n";
            return Helper(name,text);
        }
    }
}
