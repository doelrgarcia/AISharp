using AISharp.Compiler;
using System.Text;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static bool ArrayCopyReferencePossible(ScalarType from,ScalarType to)
    {
        if(from==to)return true;
        bool Reference(ScalarType type)=>IsObjectType(type)||type==ScalarType.String||IsClassType(type);
        if(!Reference(from)||!Reference(to))return false;
        if(IsObjectType(from)||IsObjectType(to))return true;
        if(from==ScalarType.String)from=layouts!.Single(s=>s.IsStringObject).Id;
        if(to==ScalarType.String)to=layouts!.Single(s=>s.IsStringObject).Id;
        var a=(int)from-ScalarLanguage.StructTypeBase;var b=(int)to-ScalarLanguage.StructTypeBase;
        if(ScalarLanguage.Derives(layouts!,a,b)||ScalarLanguage.Derives(layouts!,b,a))return true;
        var f=Layout(from);var t=Layout(to);
        return f.IsInterface&&t.IsInterface||t.IsInterface&&!f.IsSealed||f.IsInterface&&!t.IsSealed;
    }
    private sealed partial class Emitter
    {
        private string ArrayCopyHelper(ScalarType type)
        {
            usesObjects=true;var name="ai_array_copy_"+ClassTag(type);if(helpers.ContainsKey(name))return name;
            var ct=CType(type);var tag=ClassTag(type);var logical=ArrayStorageType(type);var element=CType(logical);
            var candidates=CovariantArrayCandidates(type);var declarations=new StringBuilder();var body=new StringBuilder();var declared=new HashSet<string>();
            void Prototype(string helper,string signature){if(declared.Add(helper))declarations.Append("static ").Append(signature).AppendLine(";");}
            foreach(var source in candidates)
            {
                var sourceType=ArrayStorageType(source.Id);body.Append("case ").Append(ClassTag(source.Id)).Append(": switch(destination_tag) {\n");
                foreach(var destination in candidates)
                {
                    var destinationType=ArrayStorageType(destination.Id);body.Append("case ").Append(ClassTag(destination.Id)).Append(": {\n");
                    foreach(var actual in new[]{source,destination}.DistinctBy(x=>x.Id))
                    {
                        body.Append("_Static_assert(offsetof(ai_struct_").Append(ClassTag(actual.Id)).Append(", f0)==offsetof(ai_struct_").Append(tag).Append(", f0), \"array copy length ABI\");\n");
                        body.Append("_Static_assert(offsetof(ai_struct_").Append(ClassTag(actual.Id)).Append(", ai_items)==offsetof(ai_struct_").Append(tag).Append(", ai_items), \"array copy cells ABI\");\n");
                    }
                    if(sourceType==destinationType)
                    {
                        body.Append("if(source_length!=0) memmove((unsigned char *)destination_items+(size_t)(uint32_t)index*sizeof(").Append(CType(destinationType)).Append("),source_items,(size_t)(uint32_t)source_length*sizeof(").Append(CType(sourceType)).Append(")); return;\n");
                    }
                    else if(!ArrayCopyReferencePossible(sourceType,destinationType))body.Append("ai_fault(\"System.ArrayTypeMismatchException\");\n");
                    else
                    {
                        var read=ArrayIndexHelper(type,false);var store=ArrayIndexHelper(type,true);
                        Prototype(read,element+" "+read+"("+ct+" p, int32_t i)");Prototype(store,element+" "+store+"("+ct+" p, int32_t i, "+element+" v)");
                        body.Append("for(int32_t i=0;i<source_length;i++) { ").Append(element).Append(" value=").Append(read).Append("(source,i);\n");
                        if(destinationType==ScalarType.String)
                        {
                            var wrapper=layouts!.Single(t=>t.IsStringObject);
                            body.Append("if(value!=NULL) { int32_t value_tag; memcpy(&value_tag,value,sizeof value_tag); if(value_tag!=").Append(ClassTag(wrapper.Id)).Append(") ai_fault(\"System.InvalidCastException\"); }\n");
                        }
                        else if(!IsObjectType(destinationType))
                        {
                            var belongs=IsInstance(destinationType);Prototype(belongs,"int32_t "+belongs+"(const void *p)");
                            body.Append("if(value!=NULL && !").Append(belongs).Append("((const void *)value)) ai_fault(\"System.InvalidCastException\");\n");
                        }
                        body.Append("(void)").Append(store).Append("(destination,index+i,value); } return;\n");
                    }
                    body.Append("}\n");
                }
                body.Append("default: ai_fault(\"System.InvalidProgramException\"); } break;\n");
            }
            var text=new StringBuilder();text.Append(declarations).Append("static void ").Append(name).Append('(').Append(ct).Append(" source, ").Append(ct).Append(" destination, int32_t index) {\n");
            text.Append("if(source==NULL) ai_fault(\"System.NullReferenceException\"); if(destination==NULL) ai_fault(\"System.ArgumentNullException\");\nint32_t source_length,destination_length,source_tag,destination_tag; void *source_items,*destination_items;\n");
            foreach(var which in new[]{"source","destination"})text.Append("memcpy(&").Append(which).Append("_tag,").Append(which).Append(",sizeof(int32_t)); memcpy(&").Append(which).Append("_length,(const unsigned char *)").Append(which).Append("+offsetof(ai_struct_").Append(tag).Append(",f0),sizeof(int32_t)); memcpy(&").Append(which).Append("_items,(const unsigned char *)").Append(which).Append("+offsetof(ai_struct_").Append(tag).Append(",ai_items),sizeof(void *));\n");
            text.Append("if(index<0) ai_fault(\"System.ArgumentOutOfRangeException\"); if(index>destination_length || source_length>destination_length-index) ai_fault(\"System.ArgumentException\");\nswitch(source_tag) {\n").Append(body).Append("default: ai_fault(\"System.InvalidProgramException\"); }\n}\n");
            return Helper(name,text.ToString());
        }
    }
}
