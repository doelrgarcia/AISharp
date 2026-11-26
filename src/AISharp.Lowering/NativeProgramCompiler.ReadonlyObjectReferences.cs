using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed partial class Emitter
    {
        private readonly HashSet<int> readonlyObjectSlots = [];
        private static bool ReadonlyObjectReference(ScalarType type, ScalarParameterMode mode)
            => IsObjectType(type) && mode is ScalarParameterMode.In or ScalarParameterMode.RefReadonly;
        private static string ReadonlyObjectRead(string reference) => "ai_readonly_object_read(" + reference + ")";
        private const string ReadonlyObjectReferenceRuntime = """
typedef struct {
 void * const volatile *raw;
 const void *array;
 int32_t index;
 void *(*reader)(const void *, int32_t);
} ai_readonly_object_ref;
static inline ai_readonly_object_ref ai_readonly_object_raw(void * const volatile *p) {
 ai_readonly_object_ref r = { p, NULL, 0, NULL }; return r;
}
static inline ai_readonly_object_ref ai_readonly_object_array(const void *array, int32_t index, void *(*reader)(const void *, int32_t)) {
 ai_readonly_object_ref r = { NULL, array, index, reader }; return r;
}
static inline void *ai_readonly_object_read(ai_readonly_object_ref r) {
 if(r.raw != NULL) return *r.raw;
 if(r.reader != NULL) return r.reader(r.array, r.index);
 ai_fault("System.InvalidProgramException");
}

""";
        // A readonly object alias can point either at an ordinary object cell or at
        // an array element whose physical representation needs a live reader.
        private string ReadonlyObjectAddress(Value storage)
        {
            switch(storage)
            {
                case Read read when readonlyObjectSlots.Contains(read.Slot): return RawSlot(read.Slot);
                case Call call when ReadonlyObjectReference(call.Type,call.ReturnMode): return ReferenceCallAddress(call);
                case ReferenceChoice choice when !choice.Writable: return ReferenceChoiceAddress(choice);
                case ReferenceRebind rebind when readonlyObjectSlots.Contains(rebind.Slot):
                {
                    var reference=ReadonlyObjectAddress(rebind.Storage);
                    c.Append(RawSlot(rebind.Slot)).Append(" = ").Append(reference).AppendLine(";"); return RawSlot(rebind.Slot);
                }
                case ArrayGet element:
                {
                    var array=Atom(element.Array);var index=Atom(element.Index);var savedArray=Temp();var savedIndex=Temp();
                    c.Append(CType(element.Array.Type)).Append(' ').Append(savedArray).Append(" = ").Append(array).AppendLine(";");
                    c.Append("int32_t ").Append(savedIndex).Append(" = ").Append(index).AppendLine(";");
                    c.Append("if (").Append(savedArray).AppendLine(" == NULL) ai_fault(\"System.NullReferenceException\");");
                    c.Append("if (").Append(savedIndex).Append(" < 0 || ").Append(savedIndex).Append(" >= ").Append(savedArray).AppendLine("->f0) ai_fault(\"System.IndexOutOfRangeException\");");
                    var get=ArrayIndexHelper(element.Array.Type,false);var reader="ai_readonly_array_reader_"+ClassTag(element.Array.Type);
                    Helper(reader,"static void *"+reader+"(const void *array, int32_t index) { return (void *)"+get+"(("+CType(element.Array.Type)+")array, index); }\n");
                    var result=Temp();c.Append("ai_readonly_object_ref ").Append(result).Append(" = ai_readonly_object_array(").Append(savedArray).Append(", ").Append(savedIndex).Append(", ").Append(reader).AppendLine(");");return result;
                }
                default:
                {
                    var raw=StorageAddress(storage);var result=Temp();
                    c.Append("ai_readonly_object_ref ").Append(result).Append(" = ai_readonly_object_raw(").Append(raw).AppendLine(");");return result;
                }
            }
        }
        private string ReferenceValue(Value storage, string address)
        {
            var descriptor=storage switch
            {
                Call call=>ReadonlyObjectReference(call.Type,call.ReturnMode),
                ReferenceChoice choice=>IsObjectType(choice.Type)&&!choice.Writable,
                ReferenceRebind rebind=>readonlyObjectSlots.Contains(rebind.Slot),
                _=>false
            };
            return descriptor?ReadonlyObjectRead(address):"*"+address;
        }
    }
}
