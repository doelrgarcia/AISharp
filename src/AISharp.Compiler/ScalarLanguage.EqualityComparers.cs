using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeComparerPrefix = "__native_comparer_";
    public const string NativeEqualityComparerName = NativeComparerPrefix + "base";
    public const string NativeEqualityComparerInterface = NativeComparerPrefix + "interface";
    public const string NativeDefaultComparerName = NativeComparerPrefix + "default";
    public const string NativeNonGenericComparerName = NativeComparerPrefix + "nongeneric";

    private static string AddEqualityComparerSource(string source, ScalarFeatures features)
    {
        if ((features & ScalarFeatures.Generics) == 0) return source;
        var tokens=new Lexer(source).Lex().ToArray();
        var names=tokens.Where(t=>t.Kind==TokenKind.Identifier).Select(t=>t.Text).ToHashSet(StringComparer.Ordinal);
        var declarations=Enumerable.Range(0,Math.Max(0,tokens.Length-1)).Where(i=>tokens[i].Text is "class" or "struct" or "interface").Select(i=>tokens[i+1].Text).ToHashSet(StringComparer.Ordinal);
        bool QualifiedRequest(string name)
        {
            var parts=name.Split('.');
            for(var i=0;i<tokens.Length;i++)
            {
                var j=i;var matched=true;
                foreach(var part in parts)
                {if(j>=tokens.Length||tokens[j].Kind!=TokenKind.Identifier||tokens[j++].Text!=part){matched=false;break;}if(part!=parts[^1]&&(j>=tokens.Length||tokens[j++].Text!=".")){matched=false;break;}}
                if(matched)return true;
            }
            return false;
        }
        var defaults=names.Contains("EqualityComparer")&&(!declarations.Contains("EqualityComparer")||QualifiedRequest("System.Collections.Generic.EqualityComparer"));
        if (!defaults&&(!names.Contains("IEqualityComparer")||declarations.Contains("IEqualityComparer")&&!QualifiedRequest("System.Collections.Generic.IEqualityComparer")&&!QualifiedRequest("System.Collections.IEqualityComparer"))) return source;
        const string item="__native_comparer_item";
        source+="\ninterface "+NativeEqualityComparerInterface+"<in "+item+"> {\nfn Equals(left: "+item+",right: "+item+") -> bool\nfn GetHashCode(value: "+item+") -> int\n}\n"
            +"interface "+NativeNonGenericComparerName+" {\nfn Equals(left: object,right: object) -> bool\nfn GetHashCode(value: object) -> int\n}\n";
        if (!defaults) return source;
        if ((features & ScalarFeatures.StaticInitialization) != 0) activeTypeInitialization=true;
        source+="abstract class "+NativeEqualityComparerName+"<"+item+">: "+NativeEqualityComparerInterface+"<"+item+">,"+NativeNonGenericComparerName+" {\n"
            +"private static readonly __cached: "+NativeEqualityComparerName+"<"+item+"> = new "+NativeDefaultComparerName+"<"+item+">()\n"
            +"public static Default: "+NativeEqualityComparerName+"<"+item+"> {get{return __cached}}\n"
            +"public abstract fn Equals(left: "+item+",right: "+item+") -> bool\n"
            +"public abstract fn GetHashCode(value: "+item+") -> int\n"
            +"fn "+NativeNonGenericComparerName+".Equals(left: object,right: object) -> bool\n ensures true\n{"
            +"if(left==right){return true}\nif(left==null||right==null){return false}\n"
            +"if(left is "+item+" first&&right is "+item+" second){return this.Equals(first,second)}\nthrow new ArgumentException()}\n"
            +"fn "+NativeNonGenericComparerName+".GetHashCode(value: object) -> int\n ensures true\n{"
            +"if(value==null){return 0}\nif(value is "+item+" converted){return this.GetHashCode(converted)}\nthrow new ArgumentException()}\n}\n"
            +"sealed class "+NativeDefaultComparerName+"<"+item+">: "+NativeEqualityComparerName+"<"+item+"> {\n"
            +"public override fn Equals(left: "+item+",right: "+item+") -> bool\n ensures true\n{return false}\n"
            +"public override fn GetHashCode(value: "+item+") -> int\n ensures true\n{return 0}\n}\n";
        return source;
    }

    private static bool DefaultComparerMethod(ScalarStruct? owner, string method)
        => owner is not null && (owner.Name == NativeDefaultComparerName || owner.Name.StartsWith(NativeDefaultComparerName+"<",StringComparison.Ordinal))
            && method is "Equals" or "GetHashCode";

    private sealed partial class ExtendedBinder
    {
        private bool BuiltinComparerTypeName(string name)
            => Has(ScalarFeatures.Generics) && name is "EqualityComparer" or "IEqualityComparer";

        private bool ComparerReceiver(ScalarType type)
        {
            if(!IsStruct(type))return false;
            foreach(var index in AssignableInterfaces(_structs,IndexOf(type)).Prepend(IndexOf(type)))
                if(_structs[index].Name==NativeNonGenericComparerName||_structs[index].Template>=0&&_structs[_structs[index].Template].Name==NativeEqualityComparerInterface)return true;
            return false;
        }

        private ScalarExpression? ComparerObjectMember(ScalarExpression receiver,Lexeme member,List<ScalarExpression> arguments)
        {
            if(receiver.Operation=="static-type"||!Has(ScalarFeatures.Boxing)||!ComparerReceiver(receiver.Type))return null;
            if(DeconstructMethods(StructOf(receiver.Type),member.Text).Any(m=>ApplicableDeconstruct(m,arguments,null)))return null;
            if(member.Text=="Equals"&&arguments.Count==1)
            {
                var argument=BindArgument(arguments[0],(ScalarType)ObjectTypeId,ScalarParameterMode.Value);CompleteArguments([argument]);
                return ObjectEquals(receiver,argument,member.Location,instance:true);
            }
            if(member.Text=="GetHashCode"&&arguments.Count==0)
                return new("call",ScalarType.Int,member.Location,ObjectInstanceHashTarget){Arguments=[AsObject(receiver)]};
            return null;
        }

        private ScalarType? EqualityComparerTypeText(string text,SourceLocation at,bool forceBuiltin=false)
        {
            if(text.Trim()=="IEqualityComparer"&&(forceBuiltin||StructNamedAny("IEqualityComparer")is null))
                return StructNamed(NativeNonGenericComparerName)?.Id;
            var match=Regex.Match(text.Trim(),@"^(EqualityComparer|IEqualityComparer)\s*<(.*)>$");
            if(!match.Success||!forceBuiltin&&StructNamedAny(match.Groups[1].Value) is not null)return null;
            var isDefault=match.Groups[1].Value=="EqualityComparer";
            if(isDefault&&!Has(ScalarFeatures.Boxing))Fail("AI3100","The default equality comparer requires the native object profile.",at);
            return GenericType((isDefault?NativeEqualityComparerName:NativeEqualityComparerInterface)+"<"+match.Groups[2].Value+">",at);
        }

        private ScalarFunction BindDefaultComparerMethod()
        {
            var at=_function.Location;var equal=_function.Name=="Equals";var result=equal?ScalarType.Bool:ScalarType.Int;
            var self=new ScalarParameter(Fresh(),_struct!.Id);
            var parameters=_function.Parameters.Select(p=>
            {
                var signature=SignatureParameter(p,at);var local=Declare(p.Name,signature.Type,true,at);
                _assigned.Add(local.EmittedName);return signature with{Name=local.EmittedName};
            }).ToList();
            ScalarExpression value;
            if(_checkOnly)value=new("literal",result,at,equal?"false":"0");
            else
            {
                var first=new ScalarExpression("local",parameters[0].Type,at,parameters[0].Name);
                value=equal?ObjectValueEqual(first,new("local",parameters[1].Type,at,parameters[1].Name),at):HashRecordValue(first,at);
            }
            return Erase(new(_qualifiedName!,[self,..parameters],result,[new("return",at,value)],[TrueContract(at)],at));
        }
    }
}
