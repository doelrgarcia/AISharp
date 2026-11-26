using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    // These are exact library identities, not aliases for source-owned short names.
    private static readonly IReadOnlyDictionary<string, string> QualifiedBuiltinTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["System.Object"]="object", ["System.String"]="string", ["System.Boolean"]="bool", ["System.Char"]="char",
        ["System.Byte"]="byte", ["System.SByte"]="sbyte", ["System.Int16"]="short", ["System.UInt16"]="ushort",
        ["System.Int32"]="int", ["System.UInt32"]="uint", ["System.Int64"]="long", ["System.UInt64"]="ulong",
        ["System.Single"]="f32", ["System.Double"]="f64", ["System.Decimal"]="decimal",
        ["System.IntPtr"]="nint", ["System.UIntPtr"]="nuint", ["System.Void"]="void", ["System.Type"]="Type",
        ["System.IEquatable"]=NativeEquatableName,
        ["System.Collections.Generic.EqualityComparer"]=NativeEqualityComparerName,
        ["System.Collections.Generic.IEqualityComparer"]=NativeEqualityComparerInterface,
        ["System.Collections.IEqualityComparer"]=NativeNonGenericComparerName,
        ["System.Collections.Generic.IEnumerable"]="IEnumerable", ["System.Collections.Generic.IEnumerator"]="IEnumerator",
        ["System.Collections.Generic.ICollection"]="ICollection", ["System.Collections.Generic.IList"]="IList",
        ["System.Collections.Generic.IReadOnlyCollection"]="IReadOnlyCollection", ["System.Collections.Generic.IReadOnlyList"]="IReadOnlyList"
    };

    private static string? QualifiedBuiltinAlias(string text)
    {
        var head=Regex.Replace(text.Split('<')[0],@"\s+","");
        if(head.StartsWith("global::",StringComparison.Ordinal))head=head[8..];
        if(head.Contains('.'))head=string.Join(".",head.Split('.').Select(IdentifierName));
        return QualifiedBuiltinTypes.GetValueOrDefault(head);
    }

    private sealed partial class ExtendedBinder
    {
        private bool SourceSystemType() => _typeArgs?.ContainsKey("System")==true || _memberArgs?.ContainsKey("System")==true
            || StructNamedAny("System") is { TypeParameters.Count: 0 } || EnumNamed("System") is not null;

        // Look ahead without replacing source text, so diagnostics retain the original offsets.
        private (string Name,string Alias,int End)? QualifiedBuiltinAt(int position,bool expression=false)
        {
            if(CompilationQualification(position,expression) is {} compiled)return compiled;
            if(ImportedQualification(position,expression) is {} imported)return imported;
            var start=position;var global=TokenAt(position).Text=="global";
            if(global)
            {
                position++;
                if(TokenAt(position).Text=="::")position++;
                else if(TokenAt(position).Text==":"&&TokenAt(position+1).Text==":")position+=2;
                else return null;
            }
            if(TokenAt(position).Name!="System" || !global&&(SourceSystemType() || expression&&
                (IsLocalName("System")||_struct is not null&&NameMemberExists(_struct.Id,"System")||_functions.Any(f=>f.Name=="System")||ScopedCompilationFunction("System",TokenAt(start).Location) is not null)))return null;
            var name="System";position++;
            (string Name,string Alias,int End)? found=null;
            while(TokenAt(position).Text=="."&&IsSourceIdentifier(TokenAt(position+1).Text))
            {
                name+="."+TokenAt(position+1).Name;position+=2;
                if(QualifiedBuiltinTypes.TryGetValue(name,out var alias))found=((global?"global::":"")+name,alias,position);
                if(position-start>24)break;
            }
            return found;
        }

        private Lexeme TypeHeadToken()
        {
            if(QualifiedBuiltinAt(_position) is {} qualified)
            {var at=Current.Location;_position=qualified.End;return new(qualified.Name,at);}
            return Next();
        }

        private ScalarType? QualifiedBuiltinTypeText(string text,SourceLocation at,bool allowUnit)
        {
            var compact=Regex.Replace(text,@"\s+","");var alias=QualifiedBuiltinAlias(compact);
            if(alias is null)return null;
            if(!compact.StartsWith("global::",StringComparison.Ordinal)&&SourceSystemType())
                Fail("AI3106","The source type 'System' does not contain this qualified library type.",at);
            var open=compact.IndexOf('<');var arguments=open<0?"":compact[open..];
            if(alias is "IEnumerable" or "IEnumerator" || ArrayCollectionKinds.Contains(alias))
            {
                if(open<0)Fail("AI3106","The qualified generic sequence type requires one type argument.",at);
                if(alias is "IEnumerable" or "IEnumerator")
                {
                    if(!Has(ScalarFeatures.Iterators))Fail("AI3106","Iterator types are disabled in this profile.",at);
                    return IteratorTypeText(alias+arguments,at,forceBuiltin:true);
                }
                if(!Has(ScalarFeatures.TypedArrays))Fail("AI3106","Native collection types are disabled in this profile.",at);
                return ArrayCollectionTypeText(alias+arguments,at,forceBuiltin:true);
            }
            if(alias is NativeEquatableName or NativeEqualityComparerName or NativeEqualityComparerInterface)
            {
                if(!Has(ScalarFeatures.Generics))Fail("AI3106","Generic library types are disabled in this profile.",at);
                if(open<0)Fail("AI3106","The qualified generic type requires one type argument.",at);
                if(alias==NativeEquatableName)return EquatableTypeText("IEquatable"+arguments,at,forceBuiltin:true);
                return EqualityComparerTypeText((alias==NativeEqualityComparerName?"EqualityComparer":"IEqualityComparer")+arguments,at,forceBuiltin:true);
            }
            if(open>=0)Fail("AI3106","This qualified library type does not accept type arguments.",at);
            if(alias==NativeNonGenericComparerName)
            {
                if(!Has(ScalarFeatures.Generics))Fail("AI3106","Library comparer types are disabled in this profile.",at);
                return StructNamed(alias)?.Id??throw DeclarationError("AI3106","The qualified comparer interface is unavailable.",at);
            }
            if(alias=="Type")
            {
                if(!Has(ScalarFeatures.TypeIdentity))Fail("AI3106","Runtime Type values are disabled in this profile.",at);
                return NativeType(at);
            }
            if(alias is "nint" or "nuint")
            {
                if(!Has(ScalarFeatures.Types)||!Has(ScalarFeatures.NativeIntegers))Fail("AI3106","Native integer types are disabled in this profile.",at);
                _nativeTarget.Used=true;return alias=="nint"?ScalarType.NInt:ScalarType.NUInt;
            }
            return Type(alias,at,allowUnit);
        }

        private (Lexeme Token,ScalarType Type,int End)? QualifiedTypeCast()
        {
            if(Current.Text!="("||QualifiedBuiltinAt(_position+1) is null&&EnumDefinition(TokenAt(_position+1).Text) is null&&ImportAliasType(TokenAt(_position+1).Text,TokenAt(_position+1).Location) is null)return null;
            var saved=_position;var tokens=_tokens;_tokens=[..tokens];
            try {Next();var token=TypeToken();if(!Take(")"))return null;return(token,Type(token.Text,token.Location),_position);}
            finally {_position=saved;_tokens=tokens;}
        }

        private ScalarExpression QualifiedExplicitCast(ScalarType target,ScalarExpression operand,SourceLocation at)
        {
            if(target==operand.Type)return operand;
            if(IsReference(target)||IsReference(operand.Type)||IsNull(operand.Type))return ReferenceCast(target,operand,at);
            if(IsEnum(target)||IsEnum(operand.Type))return EnumCast(target,operand,at);
            if(!IsNum(target)||!IsNum(operand.Type))Fail("AI3118",$"Cannot convert {Show(operand.Type)} to {Show(target)}.",at);
            return Fold(new("cast",target,at,Left:operand){Wraps=_wraps});
        }
    }
}
