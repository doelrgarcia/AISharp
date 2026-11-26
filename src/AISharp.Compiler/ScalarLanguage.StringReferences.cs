using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeStringObjectName=NativeObjectPrefix+"string";
    public const string NativeStringObjectField=NativeObjectPrefix+"string_value";
    private sealed partial class ExtendedBinder
    {
        private ScalarStruct StringObjectLayout(SourceLocation at)
        {
            if(_structs.FirstOrDefault(s=>s.IsStringObject) is {} known)return AttachBuiltinEquatable(known);
            var index=_structs.Count;if(index>=1000)Fail("AI3100","Too many types for native string reference storage.",at);
            var owner=new ScalarStruct(NativeStringObjectName,(ScalarType)(StructTypeBase+index),[new(NativeStringObjectField,ScalarType.String,ScalarType.String,false,true,at){Owner=index,Hidden=true}],[],at,IsClass:true){IsSealed=true,IsStringObject=true};
            _structs.Add(owner);return AttachBuiltinEquatable(owner);
        }
        private ScalarExpression StringToObject(ScalarExpression value)
        {
            if(_checkOnly)return new("null",(ScalarType)ObjectTypeId,value.Location);
            var owner=StringObjectLayout(value.Location);var reference=new ScalarExpression("string-object",owner.Id,value.Location,Left:value);
            return new("toobject",(ScalarType)ObjectTypeId,value.Location,Left:reference);
        }
        private ScalarExpression ObjectToString(ScalarExpression value,SourceLocation at,bool safe=false)
        {
            if(_checkOnly)return new("null",ScalarType.String,at);
            _=StringObjectLayout(at);return new(safe?"object-as-string":"object-string",ScalarType.String,at,Left:value);
        }
        private ScalarExpression StringObjectPayload(ScalarStruct owner,ScalarExpression value,SourceLocation at)
            =>new("field",ScalarType.String,at,"0",Left:new("downcast",owner.Id,at,Left:value));
        private (ScalarExpression Test,ScalarExpression Value)? TryStringObjectPattern(ScalarExpression subject,ScalarType tested,SourceLocation at)
        {
            if((IsObject(subject.Type)||BuiltinEquatableConversion(ScalarType.String,subject.Type))&&tested==ScalarType.String)
            {
                if(_checkOnly)return (new("literal",ScalarType.Bool,at,"true"),new("null",ScalarType.String,at));
                var value=AsObject(subject);var owner=StringObjectLayout(at);return (new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:value),ObjectToString(value,at));
            }
            if(subject.Type==ScalarType.String&&IsObject(tested))
                return (MakeBinary("!=",subject,new("null",ScalarType.String,at),at),StringToObject(subject));
            if(subject.Type==ScalarType.String&&BuiltinEquatableConversion(ScalarType.String,tested))
                return (MakeBinary("!=",subject,new("null",ScalarType.String,at),at),StringToEquatable(subject,tested));
            return null;
        }
    }
}
