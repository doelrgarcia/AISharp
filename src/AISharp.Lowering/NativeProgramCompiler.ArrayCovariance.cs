using AISharp.Compiler;
using System.Text;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static IReadOnlyList<ScalarStruct> CovariantArrayCandidates(ScalarType type)
        => layouts!.Where(s=>IsArrayType(s.Id)&&!s.IsCheckInstance&&ScalarLanguage.Derives(layouts!,(int)s.Id-ScalarLanguage.StructTypeBase,(int)type-ScalarLanguage.StructTypeBase)).ToArray();
    private sealed partial class Emitter
    {
        private bool usesCovariantArrays;
        private string CovariantArrayIndexHelper(ScalarType type,bool store)
        {
            usesCovariantArrays=true;var ct=CType(type);var element=CType(ArrayStorageType(type));var tag=ClassTag(type);var name=(store?"ai_array_put_":"ai_array_at_")+tag;
            if(helpers.ContainsKey(name))return name;
            var declarations=new StringBuilder();var body=new StringBuilder();var declared=new HashSet<string>();
            foreach(var actual in CovariantArrayCandidates(type))
            {
                var actualTag=ClassTag(actual.Id);var storage=ArrayStorageType(actual.Id);var actualCt=CType(storage);
                body.Append("case ").Append(actualTag).Append(": {\n");
                body.Append("_Static_assert(offsetof(ai_struct_").Append(actualTag).Append(", f0) == offsetof(ai_struct_").Append(tag).Append(", f0), \"array length ABI\");\n");
                body.Append("_Static_assert(offsetof(ai_struct_").Append(actualTag).Append(", ai_items) == offsetof(ai_struct_").Append(tag).Append(", ai_items), \"array cells ABI\");\n");
                if(storage==ScalarType.String)
                {
                    var wrapper=layouts!.SingleOrDefault(s=>s.IsStringObject)??throw new Rejected(actual.Location,"A covariant string array needs its verified string identity bridge.");
                    if(store)
                    {
                        body.Append("ai_str value = { NULL, 0 }; if(v != NULL) { int32_t value_tag; memcpy(&value_tag, v, sizeof value_tag); if(value_tag != ").Append(ClassTag(wrapper.Id)).Append(") ai_fault(\"System.ArrayTypeMismatchException\"); value = ((").Append(CType(wrapper.Id)).Append(")v)->f0; }\n");
                        body.Append("memcpy((unsigned char *)items + (size_t)(uint32_t)i * sizeof value, &value, sizeof value); return v;\n");
                    }
                    else
                    {
                        var bridge=StringObjectHelper(wrapper.Id);if(declared.Add(bridge))declarations.Append("static ").Append(CType(wrapper.Id)).Append(' ').Append(bridge).Append("(ai_str s);\n");
                        body.Append("ai_str value; memcpy(&value, (const unsigned char *)items + (size_t)(uint32_t)i * sizeof value, sizeof value); return (").Append(element).Append(')').Append(bridge).Append("(value);\n");
                    }
                }
                else if(store)
                {
                    if(!IsObjectType(storage))
                    {
                        var belongs=IsInstance(storage);if(declared.Add(belongs))declarations.Append("static int32_t ").Append(belongs).Append("(const void *p);\n");
                        body.Append("if(v != NULL && !").Append(belongs).Append("(v)) ai_fault(\"System.ArrayTypeMismatchException\");\n");
                    }
                    body.Append(actualCt).Append(" value = (").Append(actualCt).Append(")v; memcpy((unsigned char *)items + (size_t)(uint32_t)i * sizeof value, &value, sizeof value); return v;\n");
                }
                else body.Append(actualCt).Append(" value; memcpy(&value, (const unsigned char *)items + (size_t)(uint32_t)i * sizeof value, sizeof value); return (").Append(element).Append(")value;\n");
                body.Append("}\n");
            }
            declarations.Append("static ").Append(element).Append(' ').Append(name).Append('(').Append(ct).Append(" p, int32_t i").Append(store?", "+element+" v":"").Append(") {\n if(p == NULL) ai_fault(\"System.NullReferenceException\"); int32_t length, actual_tag; void *items;\n memcpy(&actual_tag,p,sizeof actual_tag); memcpy(&length,(const unsigned char *)p + offsetof(ai_struct_").Append(tag).Append(", f0),sizeof length);\n if(i < 0 || i >= length) ai_fault(\"System.IndexOutOfRangeException\");\n memcpy(&items,(const unsigned char *)p + offsetof(ai_struct_").Append(tag).Append(", ai_items),sizeof items); switch(actual_tag) {\n");
            declarations.Append(body).Append("default: ai_fault(\"System.InvalidProgramException\"); }\n return NULL;\n}\n");
            return Helper(name,declarations.ToString());
        }
    }
}
