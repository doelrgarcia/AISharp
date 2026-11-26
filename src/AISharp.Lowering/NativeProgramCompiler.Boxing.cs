using AISharp.Compiler;
using System.Globalization;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record BoxValueValue(ScalarType Box,ScalarType Payload,Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>Box;}
    private sealed record UnboxValueValue(ScalarType Payload,ScalarType Box,Value Operand,SourceLocation At):Value(At){public override ScalarType Type=>Payload;}
    private static ScalarType BoxStorage(ScalarType type)=>ScalarConstants.IsNative(type)?ScalarConstants.NativeStorage(type,typePointerBits):typeEnums?.FirstOrDefault(e=>e.Id==type)?.Underlying??type;
    private static bool BoxPayload(ScalarType type)=>ScalarConstants.IsInteger(type)||ScalarConstants.IsFloat(type)||type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal
        ||typeEnums?.Any(e=>e.Id==type&&!e.IsCheckInstance)==true||IsStructType(type)&&!Layout(type).IsClass&&!Layout(type).IsInterface&&!Layout(type).IsStatic&&!Layout(type).IsTypeParameter&&!Layout(type).IsCheckInstance;
    private static void ValidateValueBoxLayouts()
    {
        var all=layouts??[];
        ValidateStringObjectLayouts();
        if(all.Count(s=>s.BoxedType is not null)>128)throw new Rejected(new(1,1,0),"More than 128 native box layouts are unsupported.");
        foreach(var box in layouts??[])
        {
            if(box.IsStringObject)continue;
            if(box.IsObjectRoot)
            {
                var rootIndex=(int)box.Id-ScalarLanguage.StructTypeBase;
                if(rootIndex<0||rootIndex>=all.Count||!ReferenceEquals(all[rootIndex],box)||box.Name!=ScalarLanguage.NativeObjectPrefix+rootIndex.ToString(CultureInfo.InvariantCulture)
                    ||!box.IsClass||!box.IsSealed||box.IsReadonly||box.IsStatic||box.IsAbstract||box.IsInterface||box.IsTypeParameter||box.IsCheckInstance||box.BoxedType is not null
                    ||box.IsRecord||box.IsStringArray||box.IsDelegate||box.IsDelegateFunction||box.IsNativeType||box.IsNativeRuntimeType||box.Template>=0||box.TypeParameters.Count!=0||box.TypeArgumentList.Count!=0||box.TypeArguments is not null
                    ||box.BaseIndex>=0||box.Interfaces.Count!=0||box.Fields.Count!=0||box.StaticFields.Count!=0||box.Properties.Count!=0||box.Methods.Count!=0||box.StaticInitializers.Count!=0||box.InstanceInitializers.Count!=0
                    ||all.Any(s=>s.BaseIndex==rootIndex))throw new Rejected(box.Location,"A native Object root requires compiler-owned sealed empty storage and exact runtime identity.");
                continue;
            }
            if(box.BoxedType is not {} type)
            {
                if(box.Name.StartsWith(ScalarLanguage.NativeBoxPrefix,StringComparison.Ordinal)||box.Name.StartsWith(ScalarLanguage.NativeObjectPrefix,StringComparison.Ordinal)||box.Methods.Any(m=>m.BoxedMethodTarget is not null))
                    throw new Rejected(box.Location,"Compiler-owned box names and adapters require a verified payload layout.");
                continue;
            }
            var index=(int)box.Id-ScalarLanguage.StructTypeBase;
            if(index<0||index>=all.Count||!ReferenceEquals(all[index],box)||!BoxPayload(type)||!box.IsClass||!box.IsSealed||box.IsInterface||box.IsAbstract||box.IsStatic||box.IsReadonly||box.IsTypeParameter||box.IsCheckInstance||box.BaseIndex>=0||box.StaticFields.Count!=0
                ||box.IsRecord||box.IsStringArray||box.IsDelegate||box.IsDelegateFunction||box.IsNativeType||box.IsNativeRuntimeType||box.Template>=0||box.TypeParameters.Count!=0||box.TypeArgumentList.Count!=0
                ||box.Properties.Count!=0||box.StaticInitializers.Count!=0||box.InstanceInitializers.Count!=0
                ||box.Name!=ScalarLanguage.NativeBoxPrefix+((int)type).ToString(CultureInfo.InvariantCulture)||box.Fields.Count!=1
                ||box.Fields[0] is not {Name:ScalarLanguage.NativeBoxValueField,IsPublic:false,IsInternal:false,IsReadonly:false,Hidden:true,IsProperty:false,IsEvent:false,IsInit:false,WritePrivate:false,IsRequired:false} field||field.Owner!=index||field.Type!=type||field.StorageType!=BoxStorage(type))
                throw new Rejected(box.Location,"A native value box requires one verified logical payload and compiler-owned sealed storage.");
            var original=IsStructType(type)?Layout(type):null;
            if(all.Any(s=>s.BaseIndex==index))throw new Rejected(box.Location,"Compiler-owned native value boxes cannot be base classes.");
            if(original is null&&ScalarLanguage.BuiltinEquatablePayload(type))
            {ValidateBuiltinEquatableStorage(box,type);continue;}
            if(!box.Interfaces.SequenceEqual(original?.Interfaces??[]))throw new Rejected(box.Location,"A native value box must preserve exactly its payload's interface identities.");
            foreach(var method in box.Methods)
            {
                var targets=original?.Methods.Where(m=>m.QualifiedName==method.BoxedMethodTarget).ToArray()??[];
                var target=targets.Length==1?targets[0]:null;
                if(target is null||target.IsStatic||target.IsConstructor||target.IsLifted||target.IsRecordEqualityComparer||target.Name.StartsWith('$')
                    ||method.Owner!=index||!method.IsPublic||method.IsInternal||method.IsStatic||method.IsConstructor||method.IsStaticConstructor||method.IsReadonly||method.ReceiverMode!=ScalarParameterMode.Value
                    ||method.IsVirtual||method.IsOverride||method.IsAbstract||method.IsSealed||method.Slot is not null||method.RecordEqualityApi is not null||method.RecordDisplayApi is not null||method.IsRecordHashApi||method.IsRecordCustomEquality||method.IsRecordEqualityComparer
                    ||method.Declaration!=target.Declaration||method.InterfaceQualifier!=target.InterfaceQualifier||method.InterfaceSlot!=target.InterfaceSlot
                    ||method.IsAccessor!=target.IsAccessor||method.AccessorMember!=target.AccessorMember||method.Backing!=target.Backing||method.IsInitAccessor!=target.IsInitAccessor
                    ||!method.QualifiedName.StartsWith(box.Name+".$adapter_",StringComparison.Ordinal))
                    throw new Rejected(method.Declaration.Location,"A native box adapter must preserve the exact payload method and receiver ABI.");
            }
            if(box.Methods.Select(m=>m.QualifiedName).Distinct(StringComparer.Ordinal).Count()!=box.Methods.Count)
                throw new Rejected(box.Location,"Native box adapter identities must be unique.");
        }
        if((layouts??[]).Where(s=>s.BoxedType is not null).GroupBy(s=>s.BoxedType).Any(g=>g.Count()!=1))throw new Rejected(new(1,1,0),"A native payload type has more than one box layout.");
    }
    private sealed partial class Verifier
    {
        private Value VerifyBoxValue(ScalarExpression expression)
        {
            if(expression.Operation is not ("box-value" or "receiver-box")||expression is not {Left:not null,Right:null,Arguments:null,Wraps:false,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null,AccessorSetter:null,RuntimeTypeShape:null,RuntimeTypeProof:null}
                ||!IsClassType(expression.Type)||Layout(expression.Type).BoxedType is not {} payload||expression.Value!=((int)payload).ToString(CultureInfo.InvariantCulture))
                throw new Rejected(expression.Location,"Boxing requires its exact compiler-owned payload layout.");
            var storage=BoxStorage(payload);return new BoxValueValue(expression.Type,storage,Require(VerifyValue(expression.Left,expression.Location),storage,expression.Location),expression.Location);
        }
        private Value VerifyUnboxValue(ScalarExpression expression)
        {
            if(expression is not {Left:not null,Right:null,Arguments:null,Wraps:false,ReferenceMode:ScalarParameterMode.Value,ReferenceExcludedArguments:null,AccessorSetter:null,RuntimeTypeShape:null,RuntimeTypeProof:null}
                ||!int.TryParse(expression.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var id)||expression.Value!=id.ToString(CultureInfo.InvariantCulture)||!BoxPayload((ScalarType)id)||expression.Type!=BoxStorage((ScalarType)id))
                throw new Rejected(expression.Location,"Unboxing requires an exact logical value type and matching native storage.");
            var box=layouts!.SingleOrDefault(s=>s.BoxedType==(ScalarType)id)??throw new Rejected(expression.Location,"Unboxing has no verified payload layout.");var value=VerifyValue(expression.Left,expression.Location);
            if(!IsObjectType(value.Type)&&(!IsClassType(value.Type)||!Layout(value.Type).IsInterface))throw new Rejected(expression.Location,"Unboxing needs an object or interface reference.");
            return new UnboxValueValue(expression.Type,box.Id,value,expression.Location);
        }
    }
    private sealed partial class Emitter
    {
        private string EmitBoxValue(BoxValueValue box)
        {
            var value=Atom(box.Operand);var result=Temp();c.Append(CType(box.Type)).Append(' ').Append(result).Append(" = ").Append(NewObject(box.Type,false)).AppendLine("();");
            c.Append(result).Append("->f0 = ").Append(value).AppendLine(";");return result;
        }
        private string EmitUnboxValue(UnboxValueValue value)
        {
            var pointer=Atom(value.Operand);var result=Temp();c.Append("if (").Append(pointer).AppendLine(" == NULL) ai_fault(\"System.NullReferenceException\");");
            var target=Layout(value.Box).BoxedType!.Value;
            bool Compatible(ScalarType source)
            {
                if(source==target)return true;
                var from=typeEnums?.FirstOrDefault(e=>e.Id==source);var to=typeEnums?.FirstOrDefault(e=>e.Id==target);
                return (from is not null||to is not null)&&(from?.Underlying??source)==(to?.Underlying??target);
            }
            var compatible=layouts!.Where(s=>s.BoxedType is {} payload&&Compatible(payload)).ToArray();
            if(compatible.Length>1)
            {
                c.Append(CType(value.Type)).Append(' ').Append(result).AppendLine(";");c.Append("switch (*(const int32_t *)").Append(pointer).AppendLine(") {");
                foreach(var candidate in compatible)c.Append("case ").Append(ClassTag(candidate.Id)).Append(": ").Append(result).Append(" = ((").Append(CType(candidate.Id)).Append(')').Append(pointer).AppendLine(")->f0; break;");
                c.Append("default: ai_fault(\"System.InvalidCastException\"); ").Append(result).AppendLine(" = 0; break; }");return result;
            }
            c.Append("if (*(const int32_t *)").Append(pointer).Append(" != ").Append(ClassTag(value.Box)).AppendLine(") ai_fault(\"System.InvalidCastException\");");
            c.Append(CType(value.Type)).Append(' ').Append(result).Append(" = ((").Append(CType(value.Box)).Append(')').Append(pointer).AppendLine(")->f0;");return result;
        }
    }
}
