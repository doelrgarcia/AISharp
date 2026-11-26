using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeBoxPrefix="__native_box_";
    public const string NativeBoxValueField="__native_box_value";
    public const string NativeObjectPrefix="__native_object_";
    private static Diagnostic? ValidateBoxingSource(string source,ScalarFeatures features)
    {
        if((features&(ScalarFeatures.Boxing|ScalarFeatures.TypedArrays))==0)return null;
        var token=new Lexer(source).Lex().FirstOrDefault(t=>t.Kind==TokenKind.Identifier&&((features&ScalarFeatures.Boxing)!=0&&(IdentifierName(t.Text).StartsWith(NativeBoxPrefix,StringComparison.Ordinal)||IdentifierName(t.Text).StartsWith(NativeObjectPrefix,StringComparison.Ordinal))||(features&ScalarFeatures.TypedArrays)!=0&&IdentifierName(t.Text).StartsWith(NativeArrayPrefix,StringComparison.Ordinal)));
        return token is null?null:new("AI3106",DiagnosticSeverity.Error,"This identifier is reserved for compiler-owned native object storage.",token.Location);
    }
    private sealed partial class ExtendedBinder
    {
        private bool Boxable(ScalarType type)=>ScalarConstants.IsInteger(type)||ScalarConstants.IsFloat(type)||type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal
            ||IsEnum(type)||IsStruct(type)&&!IsObject(type)&&IndexOf(type)>=0&&IndexOf(type)<_structs.Count&&StructOf(type) is {IsClass:false,IsInterface:false,IsStatic:false,IsTypeParameter:false,IsCheckInstance:false};
        private ScalarStruct ValueBox(ScalarType type,SourceLocation at)
        {
            if(!Boxable(type))Fail("AI3100","This type has no supported native boxing representation.",at);
            if(_structs.FirstOrDefault(s=>s.BoxedType==type) is {} known)return AttachBuiltinEquatable(known);
            if(_structs.Count(s=>s.BoxedType is not null)>=128)Fail("AI3100","More than 128 native value-box payload types are unsupported.",at);
            var index=_structs.Count;if(index>=1000)Fail("AI3100","Too many types for native value-box storage.",at);
            var id=(ScalarType)(StructTypeBase+index);var name=NativeBoxPrefix+((int)type).ToString(CultureInfo.InvariantCulture);
            var original=IsStruct(type)?StructOf(type):null;
            var box=new ScalarStruct(name,id,[new(NativeBoxValueField,type,EraseType(type),false,false,at){Owner=index,Hidden=true}],[],at,IsClass:true)
                {IsSealed=true,BoxedType=type,Interfaces=original?.Interfaces??[],TypeArguments=original?.TypeArguments};
            _structs.Add(box);
            if(original is not null)
            {
                var methods=new List<ScalarStructMethod>();
                foreach(var method in original.Methods)
                {
                    if(method.IsStatic||method.IsConstructor||method.IsLifted||method.IsRecordEqualityComparer||method.Name.StartsWith('$'))continue;
                    methods.Add(method with{QualifiedName=name+".$adapter_"+methods.Count,Owner=index,IsPublic=true,IsInternal=false,IsReadonly=false,ReceiverMode=ScalarParameterMode.Value,
                        IsVirtual=false,IsOverride=false,IsAbstract=false,IsSealed=false,Slot=null,IsRecordCustomEquality=false,RecordEqualityApi=null,RecordDisplayApi=null,IsRecordHashApi=false,
                        BoxedMethodTarget=method.QualifiedName});
                }
                box=box with{Methods=methods};_structs[index]=box;
            }
            return AttachBuiltinEquatable(box);
        }
        private ScalarExpression BoxValue(ScalarExpression value,ScalarType target,bool receiver=false)
        {
            if(_checkOnly)return new("null",target,value.Location);
            var box=ValueBox(value.Type,value.Location);ScalarExpression made=new(receiver?"receiver-box":"box-value",box.Id,value.Location,((int)value.Type).ToString(CultureInfo.InvariantCulture),Left:value);
            return IsObject(target)?new("toobject",target,value.Location,Left:made):Upcast(made,target);
        }
        private ScalarExpression UnboxValue(ScalarExpression value,ScalarType target,SourceLocation at)
        {
            if(_checkOnly)return DefaultValue(target,at);
            _=ValueBox(target,at);return new("unbox-value",target,at,((int)target).ToString(CultureInfo.InvariantCulture),Left:value);
        }
        private (ScalarExpression Test,ScalarExpression Value)? TryBoxPattern(ScalarExpression subject,ScalarType tested,SourceLocation at)
        {
            if(!Has(ScalarFeatures.Boxing))return null;
            if(_checkOnly&&IsTypeParameterType(tested)&&IsObject(subject.Type))
                return (new("literal",ScalarType.Bool,at,"true"),DefaultValue(tested,at));
            if(TryStringObjectPattern(subject,tested,at) is {} text)return text;
            if(IsReference(subject.Type)&&Boxable(tested))
            {
                var possible=IsObject(subject.Type)||IsClass(subject.Type)&&StructOf(subject.Type).IsInterface&&ValueInterfaceConversion(tested,subject.Type);
                if(!possible)Fail("AI3107","The boxed value type cannot satisfy the subject's reference type.",at);
                if(_checkOnly)return (new("literal",ScalarType.Bool,at,"true"),DefaultValue(tested,at));
                var box=ValueBox(tested,at);return (new("istype",ScalarType.Bool,at,((int)box.Id).ToString(CultureInfo.InvariantCulture),Left:subject),UnboxValue(subject,tested,at));
            }
            if(Boxable(subject.Type)&&IsReference(tested))
            {
                var possible=IsObject(tested)||IsClass(tested)&&StructOf(tested).IsInterface&&ValueInterfaceConversion(subject.Type,tested);
                if(!possible)Fail("AI3107","The value type cannot satisfy this reference pattern.",at);
                return (new("literal",ScalarType.Bool,at,"true"),BoxValue(subject,tested));
            }
            return null;
        }
        private ScalarFunction BindBoxAdapter()
        {
            if(_boxedMethodTarget==NativeBuiltinEquatableAdapter)return BindBuiltinEquatableAdapter();
            var box=_struct!;var owner=StructOf(box.BoxedType!.Value);var target=owner.Methods.Single(m=>m.QualifiedName==_boxedMethodTarget);var at=_function.Location;
            var self=new ScalarParameter(Fresh(),box.Id);var read=new ScalarExpression("local",box.Id,at,self.Name);
            var payload=new ScalarExpression("field",owner.Id,at,"0",Left:read);var parameters=_function.Parameters.Select(p=>SignatureParameter(p,at)).ToList();var result=Type(_function.ReturnType,at,true);var mode=ParameterPassing(_function.ReturnType).Mode;
            var generic=GenericOf(target.Declaration);var arguments=generic?.Parameters.Select(p=>_typeArgs![p.Name]).ToArray();
            var call=InterfaceImplementationCall(target,payload,parameters.Select(p=>ForwardParameter(p,at)).ToList(),result,at,arguments);
            var function=new ScalarFunction(_qualifiedName!,[self,..parameters],result,result==ScalarType.Unit?[new("expression",at,call)]:[Returning(call,mode,at)],[TrueContract(at)],at){ReturnMode=mode};
            return Erase(function);
        }
    }
}
