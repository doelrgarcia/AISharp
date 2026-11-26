using AISharp.Compiler;
using System.Text;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record StringObjectValue(ScalarType Storage,Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>Storage;}
    private sealed record ObjectStringValue(ScalarType Storage,Value Operand,bool Safe,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.String;}
    private static void ValidateStringObjectLayouts()
    {
        var all=layouts??[];var strings=all.Where(s=>s.IsStringObject).ToArray();
        if(strings.Length>1)throw new Rejected(new(1,1,0),"A native string reference requires one storage identity.");
        foreach(var owner in strings)
        {
            var index=(int)owner.Id-ScalarLanguage.StructTypeBase;
            if(index<0||index>=all.Count||!ReferenceEquals(all[index],owner)||owner.Name!=ScalarLanguage.NativeStringObjectName
                ||!owner.IsClass||!owner.IsSealed||owner.IsReadonly||owner.IsStatic||owner.IsAbstract||owner.IsInterface||owner.IsTypeParameter||owner.IsCheckInstance||owner.BoxedType is not null||owner.IsObjectRoot
                ||owner.IsRecord||owner.IsStringArray||owner.IsDelegate||owner.IsDelegateFunction||owner.IsNativeType||owner.IsNativeRuntimeType||owner.RuntimeTypeDefinition is not null
                ||owner.Template>=0||owner.TypeParameters.Count!=0||owner.TypeArgumentList.Count!=0||owner.TypeArguments is not null||owner.BaseIndex>=0
                ||owner.StaticFields.Count!=0||owner.Properties.Count!=0||owner.StaticInitializers.Count!=0||owner.InstanceInitializers.Count!=0||owner.Fields.Count!=1
                ||owner.Fields[0] is not {Name:ScalarLanguage.NativeStringObjectField,Type:ScalarType.String,StorageType:ScalarType.String,IsPublic:false,IsInternal:false,IsReadonly:true,Hidden:true,IsProperty:false,IsEvent:false,IsInit:false,WritePrivate:false,IsRequired:false} field||field.Owner!=index
                ||all.Any(s=>s.BaseIndex==index))throw new Rejected(owner.Location,"A native string reference requires compiler-owned sealed immutable UTF-16 storage.");
            ValidateBuiltinEquatableStorage(owner,ScalarType.String);
        }
    }
    private sealed partial class Verifier
    {
        private Value VerifyStringObject(ScalarExpression expression)
        {
            if(expression is not {Operation:"string-object",Value:null,Left:not null,Right:null,Arguments:null,Wraps:false,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null,AccessorSetter:null,RuntimeTypeShape:null,RuntimeTypeProof:null}
                ||!IsClassType(expression.Type)||!Layout(expression.Type).IsStringObject)
                throw new Rejected(expression.Location,"A string reference conversion requires exact compiler-owned storage and one UTF-16 value.");
            return new StringObjectValue(expression.Type,Require(VerifyValue(expression.Left,expression.Location),ScalarType.String,expression.Location),expression.Location);
        }
        private Value VerifyObjectString(ScalarExpression expression)
        {
            if(expression is not {Operation:"object-string" or "object-as-string",Type:ScalarType.String,Value:null,Left:not null,Right:null,Arguments:null,Wraps:false,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null,AccessorSetter:null,RuntimeTypeShape:null,RuntimeTypeProof:null})
                throw new Rejected(expression.Location,"A string reference cast requires one object and no unrelated metadata.");
            var storage=layouts!.SingleOrDefault(s=>s.IsStringObject)??throw new Rejected(expression.Location,"A string cast has no verified native storage.");
            return new ObjectStringValue(storage.Id,Require(VerifyValue(expression.Left,expression.Location),(ScalarType)ScalarLanguage.ObjectTypeId,expression.Location),expression.Operation=="object-as-string",expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private bool usesStringReference;
        private string EmitStringObject(StringObjectValue value)
        {
            var operand=Atom(value.Operand);var ct=CType(value.Storage);var name=StringObjectHelper(value.Storage);
            var result=Temp();c.Append(ct).Append(' ').Append(result).Append(" = ").Append(name).Append('(').Append(operand).AppendLine(");");return result;
        }
        private string StringObjectHelper(ScalarType storage)
        {
            usesStringReference=true;var ct=CType(storage);var create=NewObject(storage,false);const string name="ai_z_string_object";
            if(!helpers.ContainsKey(name))
            {
                var text=new StringBuilder("typedef struct ai_string_reference_entry { struct ai_string_reference_entry *next; const uint16_t *p; int32_t n; "+ct+"value; } ai_string_reference_entry;\n");
                text.Append("static SRWLOCK ai_string_reference_lock = SRWLOCK_INIT;\nstatic ai_string_reference_entry *ai_string_reference_buckets[4096] = { NULL };\n");
                text.Append("static ").Append(ct).Append(name).Append("(ai_str s) {\n if (s.p == NULL) return NULL;\n uintptr_t identity = s.n == 0 ? (uintptr_t)0 : (uintptr_t)s.p;\n size_t bucket = (size_t)((identity >> 3) ^ (identity >> 17)) & (size_t)4095;\n");
                text.Append(" AcquireSRWLockExclusive(&ai_string_reference_lock);\n for (ai_string_reference_entry *e = ai_string_reference_buckets[bucket]; e != NULL; e = e->next) {\n  if ((s.n == 0 && e->n == 0) || (e->p == s.p && e->n == s.n)) { ").Append(ct).Append("found = e->value; ReleaseSRWLockExclusive(&ai_string_reference_lock); return found; }\n }\n ReleaseSRWLockExclusive(&ai_string_reference_lock);\n");
                // Allocation can throw. Hold the cache lock only while examining or linking initialized entries.
                text.Append(' ').Append(ct).Append("created = ").Append(create).Append("();\n created->f0 = s;\n ai_string_reference_entry *fresh = (ai_string_reference_entry *)ai_arena_alloc(sizeof *fresh);\n fresh->p = s.p; fresh->n = s.n; fresh->value = created;\n");
                text.Append(" AcquireSRWLockExclusive(&ai_string_reference_lock);\n for (ai_string_reference_entry *e = ai_string_reference_buckets[bucket]; e != NULL; e = e->next) {\n  if ((s.n == 0 && e->n == 0) || (e->p == s.p && e->n == s.n)) { ").Append(ct).Append("found = e->value; ReleaseSRWLockExclusive(&ai_string_reference_lock); return found; }\n }\n fresh->next = ai_string_reference_buckets[bucket]; ai_string_reference_buckets[bucket] = fresh;\n ReleaseSRWLockExclusive(&ai_string_reference_lock);\n return created;\n}\n");
                Helper(name,text.ToString());
            }
            return name;
        }
        private string EmitObjectString(ObjectStringValue value)
        {
            var operand=Atom(value.Operand);var name=value.Safe?"ai_z_object_as_string":"ai_z_object_string";
            if(!helpers.ContainsKey(name))
            {
                var wrong=value.Safe?"return empty;":"ai_fault(\"System.InvalidCastException\");";
                Helper(name,"static ai_str "+name+"(const void *value) { ai_str empty = { NULL, 0 }; if (value == NULL) return empty; int32_t tag; memcpy(&tag,value,sizeof tag); if (tag != "+ClassTag(value.Storage)+") { "+wrong+" } return (("+CType(value.Storage)+")value)->f0; }\n");
            }
            var result=Temp();c.Append("ai_str ").Append(result).Append(" = ").Append(name).Append('(').Append(operand).AppendLine(");");return result;
        }
    }
}
