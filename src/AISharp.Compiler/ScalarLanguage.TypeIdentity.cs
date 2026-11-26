using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeTypeClassName="__native_type_Type";
    public const string NativeRuntimeTypeClassName="__native_type_RuntimeType";
    public const string NativeTypeIdentityField="__native_type_id";
    private const string NativeTypeSource="\nabstract class "+NativeTypeClassName+" { public override fn GetHashCode() -> int\n ensures true\n{ return 0 }\npublic override fn ToString() -> string\n ensures true\n{ return \"\" }\npublic fn Equals(other: object) -> bool\n ensures true\n{return this==other} }\nsealed class "+NativeRuntimeTypeClassName+": "+NativeTypeClassName+" { private readonly "+NativeTypeIdentityField+": int }\n";
    private static string AddTypeIdentitySource(string source,ScalarFeatures features,out Diagnostic? diagnostic)
    {
        diagnostic=null;
        if((features&ScalarFeatures.TypeIdentity)==0)return source;
        var tokens=new Lexer(source).Lex();var reserved=tokens.FirstOrDefault(t=>t.Kind==TokenKind.Identifier&&IdentifierName(t.Text).StartsWith("__native_type_",StringComparison.Ordinal));
        if(reserved is not null){diagnostic=new("AI3106",DiagnosticSeverity.Error,"This identifier is reserved for native type identity metadata.",reserved.Location);return source;}
        return tokens.Any(t=>t.Kind==TokenKind.Identifier&&(t.Text=="typeof"||IdentifierName(t.Text) is "GetType" or "EqualityContract" or "Type"))?source+NativeTypeSource:source;
    }
    private static void ValidateNativeTypeInheritance()
    {
        if((activeFeatures&ScalarFeatures.TypeIdentity)==0)return;
        foreach(var owner in activeStructs!)
            if(owner.BaseIndex>=0&&activeStructs[owner.BaseIndex].IsNativeType&&!owner.IsNativeRuntimeType)
                throw DeclarationError("AI3100","Deriving a source class from System.Type requires the reflection/Type subclass implementation.",owner.Location);
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarType NativeType(SourceLocation at)
        {
            var type=_structs.FirstOrDefault(s=>s.IsNativeType);
            if(type is null)Fail("AI3106","Native type metadata is unavailable in this source/profile.",at);
            return type!.Id;
        }
        private ScalarExpression TypeOfExpression()
        {
            if(Has(ScalarFeatures.TypeShapes))return TypeShapeOfExpression();
            var keyword=Next();Expect("(");var written=TypeToken();ScalarType type;
            var open=System.Text.RegularExpressions.Regex.Match(written.Text,@"^(" + IdentifierSpelling.Pattern + @")<([,\s]*)>$");
            if(open.Success)
            {
                var definition=StructNamedAny(open.Groups[1].Value);
                var enumeration=EnumDefinition(open.Groups[1].Value);
                if((definition?.TypeParameters.Count??enumeration?.TypeParameters.Count)!=open.Groups[2].Value.Count(c=>c==',')+1)Fail("AI3116","The unbound generic type name does not match a declared generic arity.",written.Location);
                type=definition?.Id??enumeration!.Id;
            }
            else type=Type(written.Text,written.Location,allowUnit:true,allowStatic:true);
            Expect(")");
            return new("type-literal",NativeType(keyword.Location),keyword.Location,((int)type).ToString(CultureInfo.InvariantCulture));
        }
        private ScalarStructMethod? SourceParameterlessGetType(ScalarType type)
        {
            bool Applicable(ScalarStructMethod method)=>method.Name=="GetType"&&!method.IsAccessor&&!method.IsConstructor&&method.Declaration.Parameters.Count==0&&GenericOf(method.Declaration) is null;
            var owner=StructOf(type);
            if(owner.IsInterface)return InterfaceMethods([IndexOf(type)],"GetType").FirstOrDefault(Applicable);
            for(var index=IndexOf(type);index>=0;index=_structs[index].BaseIndex)
                if(_structs[index].Methods.FirstOrDefault(Applicable) is {} method)return method;
            return null;
        }
        private bool SourceHidesNativeGetType(ScalarType type,bool parameterless)
        {
            if(!parameterless)return NameMemberExists(type,"GetType");
            return StructOf(type).Fields.Any(f=>f.Name=="GetType"&&!f.Hidden)||FindStaticField(IndexOf(type),"GetType") is not null
                ||FindProperty(IndexOf(type),"GetType") is not null||SourceParameterlessGetType(type) is not null;
        }
        private bool NativeGetTypeAt(ScalarExpression receiver)=>Has(ScalarFeatures.TypeIdentity)&&Current.Text=="."&&TokenAt(_position+1).Name=="GetType"&&TokenAt(_position+2).Text=="("
            &&(!IsStruct(receiver.Type)||!SourceHidesNativeGetType(receiver.Type,TokenAt(_position+3).Text==")"));
        private ScalarExpression NativeGetTypeCall(ScalarExpression receiver)
        {
            Next();var member=Next();return BindNativeGetType(receiver,member);
        }
        private ScalarExpression BindNativeGetType(ScalarExpression receiver,Lexeme member)
        {
            Expect("(");if(!Take(")"))Fail("AI3116","GetType expects no arguments.",member.Location);
            ReadNavigationReceiver(receiver);
            return new("runtime-type",NativeType(member.Location),member.Location,((int)receiver.Type).ToString(CultureInfo.InvariantCulture),Left:receiver);
        }
        private bool ImplicitNativeGetTypeAt()=>Has(ScalarFeatures.TypeIdentity)&&Current.Text=="GetType"&&TokenAt(_position+1).Text=="("&&_this is not null&&_struct is not null&&!IsLocalName("GetType")&&!SourceHidesNativeGetType(_struct.Id,TokenAt(_position+2).Text==")");
        private ScalarExpression ImplicitNativeGetType()
        {
            var member=Next();return BindNativeGetType(new("local",_struct!.Id,member.Location,_this!.EmittedName),member);
        }
        private List<ScalarStatement> BindNativeTypeHash()
        {
            _=Block(false);
            var at=_function.Location;var self=new ScalarExpression("local",_struct!.Id,at,_this!.EmittedName);
            return [new("return",at,new("type-hash",ScalarType.Int,at,Left:self))];
        }
        private List<ScalarStatement> BindNativeTypeName()
        {
            _=Block(false);var at=_function.Location;var self=new ScalarExpression("local",_struct!.Id,at,_this!.EmittedName);
            return [new("return",at,new("type-name",ScalarType.String,at,Left:self))];
        }
    }
}
