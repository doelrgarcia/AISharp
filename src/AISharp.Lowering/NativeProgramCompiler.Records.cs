using System.Globalization;
using System.Text;
using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record RecordEqualityTarget(int Owner,int Function);
    private sealed record RecordEqual(Value Left,Value Right,IReadOnlyList<RecordEqualityTarget> Comparers,string Mode,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.Bool;}
    private sealed record RecordContractEqual(Value Left,Value Right,SourceLocation At):Value(At){public override ScalarType Type=>ScalarType.Bool;}
    private static bool ComparableRecordLayout(ScalarType type,HashSet<ScalarType> stack)
    {
        if(IsObjectType(type))return false;
        if(!IsStructType(type))return true;
        var info=Layout(type);if(info.IsInterface)return false;if(info.IsClass&&!info.IsRecord)return true;
        if(info.IsRecord&&ScalarLanguage.CustomRecordEquality(info)is not null)return true;
        if(!stack.Add(type))return false;
        try{return info.Fields.Where(f=>!info.IsRecord||info.BaseIndex<0||f.Owner==(int)type-ScalarLanguage.StructTypeBase).All(f=>ComparableRecordLayout(f.StorageType,stack))&&(!info.IsRecord||info.BaseIndex<0||ComparableRecordLayout(layouts![info.BaseIndex].Id,stack));}finally{stack.Remove(type);}
    }
    private sealed partial class Verifier
    {
        private RecordContractEqual VerifyRecordContractEqual(ScalarExpression expression)
        {
            var at=expression.Location;
            if(expression is not{Operation:"record-contract-equal",Type:ScalarType.Bool,Left:not null,Right:not null,Arguments:null,Value:null}
                ||expression.Left.Type!=expression.Right.Type||!IsClassType(expression.Left.Type)||!Layout(expression.Left.Type).IsRecord)
                throw new Rejected(at,"A record equality contract requires two operands of the same declared record class type.");
            return new(Require(VerifyValue(expression.Left,at),expression.Left.Type,at),Require(VerifyValue(expression.Right,at),expression.Right.Type,at),at);
        }
        private RecordCopy VerifyRecordCopy(ScalarExpression expression)
        {
            var at=expression.Location;
            if(expression is not{Operation:"record-copy",Left:not null,Right:null,Arguments:null,Value:null}||!IsStructType(expression.Type)||!Layout(expression.Type).IsRecord)
                throw new Rejected(at,"A record copy requires a declared record type and one source operand.");
            if(IsClassType(expression.Type))
                foreach(var index in ScalarLanguage.ConcreteOf(layouts!,(int)expression.Type-ScalarLanguage.StructTypeBase))
                    if(!layouts![index].IsRecord)throw new Rejected(at,"A record copy cannot include a non-record runtime subtype.");
            var constructors=new List<RecordEqualityTarget>();
            if(IsClassType(expression.Type))
                foreach(var index in ScalarLanguage.ConcreteOf(layouts!,(int)expression.Type-ScalarLanguage.StructTypeBase))
                {
                    var owner=layouts![index];var copy=owner.Methods.SingleOrDefault(m=>m.IsConstructor&&m.RecordConstructorKind=="copy");
                    if(copy is null||!signatures.TryGetValue(ScalarLanguage.RecordConstructorInitName(copy),out var signature)||signature.Return!=owner.Id||signature.ReturnMode!=ScalarParameterMode.Value||signature.Parameters.Count!=2||signature.Parameters.Any(t=>t!=owner.Id)||signature.Modes.Count!=2||signature.Modes.Any(m=>m!=ScalarParameterMode.Value)||signature.Scoped.Count!=2||signature.Scoped.Any(v=>v))
                        throw new Rejected(at,"A record clone requires a verified copy constructor for every concrete runtime type.");
                    Callees.Add(signature.Index);constructors.Add(new(index,signature.Index));
                }
            var origin=Require(VerifyValue(expression.Left,at),expression.Type,at);return new(origin,at){CopyConstructors=constructors};
        }
        private RecordEqual VerifyRecordEqual(ScalarExpression expression)
        {
            var at=expression.Location;
            if(expression is not {Operation:"record-equal" or "record-equals" or "record-impl-equals",Type:ScalarType.Bool,Left:not null,Right:not null,Arguments:null,Value:null}
                ||!IsClassType(expression.Left.Type)||!Layout(expression.Left.Type).IsRecord
                ||(expression.Operation=="record-equals"?!(IsObjectType(expression.Right.Type)||IsClassType(expression.Right.Type)):expression.Left.Type!=expression.Right.Type))
                throw new Rejected(at,"Record equality requires a record class receiver and operands matching its declared equality mode.");
            var direct=expression.Operation=="record-impl-equals";
            if(direct&&ScalarLanguage.CustomRecordEquality(Layout(expression.Left.Type)) is not null)
                throw new Rejected(at,"A custom record Equals body cannot use synthesized implementation equality.");
            var targets=new List<RecordEqualityTarget>();
            var declared=(int)expression.Left.Type-ScalarLanguage.StructTypeBase;
            foreach(var k in direct?new[]{declared}:ScalarLanguage.ConcreteOf(layouts!,declared))
            {
                var owner=layouts![k];if(!ComparableRecordLayout(owner.Id,[]))throw new Rejected(at,"Record equality requires supported, nonrecursive stored field types.");
                var method=owner.Methods.SingleOrDefault(m=>m.IsRecordEqualityComparer&&m.Owner==k);
                if(method is null||!method.IsStatic||!signatures.TryGetValue(method.QualifiedName,out var comparer)||comparer.Return!=ScalarType.Bool||comparer.Parameters.Count!=2||comparer.Parameters.Any(t=>t!=owner.Id)||comparer.Modes.Count!=2||comparer.Modes.Any(m=>m!=ScalarParameterMode.Value))
                    throw new Rejected(at,"Record equality requires a verified comparer for every concrete runtime record type.");
                Callees.Add(comparer.Index);targets.Add(new(k,comparer.Index));
            }
            var left=Require(VerifyValue(expression.Left,at),expression.Left.Type,at);var right=Require(VerifyValue(expression.Right,at),expression.Right.Type,at);
            return new(left,right,targets,expression.Operation,at);
        }
    }
    private sealed partial class Emitter
    {
        private string RecordClone(ScalarType type,IReadOnlyList<RecordEqualityTarget> constructors)
        {
            usesObjects=true;var name="ai_record_clone_"+ClassTag(type);if(helpers.ContainsKey(name))return name;
            var text=new StringBuilder("static "+CType(type)+name+"("+CType(type)+"source) {\nif (source == NULL) ai_fault(\"System.NullReferenceException\");\nint32_t tag; memcpy(&tag, source, sizeof tag);\nswitch (tag) {\n");
            foreach(var index in ScalarLanguage.ConcreteOf(layouts!, (int)type-ScalarLanguage.StructTypeBase))
            {
                var concrete=layouts![index];if(!concrete.IsRecord)throw new InvalidOperationException("A record clone has a non-record subtype.");
                var allocate=NewObject(concrete.Id,false);var ct=CType(concrete.Id);
                var target=constructors.Single(t=>t.Owner==index);var function=FunctionName(target.Function);
                helpers.TryAdd("a_record_copy_declaration_"+target.Function.ToString(CultureInfo.InvariantCulture),"static "+ct+function+"("+ct+"copy, "+ct+"source);\n");
                text.Append("case ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(": { ").Append(ct).Append("copy = ").Append(allocate).Append("(); return (").Append(CType(type)).Append(')').Append(function).Append("(copy, (").Append(ct).AppendLine(")source); }");
            }
            text.Append("default: ai_fault(\"System.InvalidCastException\"); return NULL;\n}\n}\n");return Helper(name,text.ToString());
        }
        private string RecordEqualHelper(ScalarType type,IReadOnlyList<RecordEqualityTarget> targets,string mode)
        {
            var key=mode.Replace('-','_')+"_"+ClassTag(type);var name="ai_"+key;if(helpers.ContainsKey(name))return name;
            var ct=CType(type);helpers.Add(name,"");helpers.TryAdd("a_record_equal_declaration_"+key,"static int32_t "+name+"("+ct+"a, const void *b);\n");
            var text=new StringBuilder("static int32_t "+name+"("+ct+"a, const void *b) {\n");
            if(mode=="record-equal")text.Append("if ((const void *)a == b) return 1;\nif (a == NULL) return 0;\n");
            else text.Append("if (a == NULL) ai_fault(\"System.NullReferenceException\");\n");
            if(mode!="record-impl-equals")text.Append("int32_t ta; memcpy(&ta,a,sizeof ta);\n");
            if(mode!="record-impl-equals")text.Append("switch (ta) {\n");
            foreach(var target in targets)
            {
                var concrete=layouts![target.Owner];var ctype=CType(concrete.Id);var function=FunctionName(target.Function);
                helpers.TryAdd("a_record_fields_declaration_"+target.Function.ToString(CultureInfo.InvariantCulture),"static int32_t "+function+"("+ctype+"a, "+ctype+"b);\n");
                if(mode!="record-impl-equals")text.Append("case ").Append(target.Owner.ToString(CultureInfo.InvariantCulture)).Append(": ");
                text.Append("return ");
                if(mode!="record-impl-equals")
                {
                    var belongs=IsInstance(concrete.Id);text.Append(function).Append("((").Append(ctype).Append(")a, ").Append(belongs).Append("((const void *)b) ? (").Append(ctype).AppendLine(")b : NULL);");
                }
                else text.Append(function).Append("((").Append(ctype).Append(")a, (").Append(ctype).AppendLine(")b);");
            }
            if(mode!="record-impl-equals")text.Append("default: return 0;\n}\n");
            text.Append("}\n");helpers[name]=text.ToString();return name;
        }
        private string RecordContractHelper(ScalarType type)
        {
            var name="ai_record_contract_"+ClassTag(type);var ct=CType(type);
            return helpers.ContainsKey(name)?name:Helper(name,"static int32_t "+name+"("+ct+"a, "+ct+"b) {\nif (a == NULL || b == NULL) ai_fault(\"System.NullReferenceException\");\nint32_t ta, tb; memcpy(&ta,a,sizeof ta); memcpy(&tb,b,sizeof tb); return ta == tb;\n}\n");
        }
    }
}
