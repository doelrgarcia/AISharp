using System.Text;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeCollectionPrefix="__native_collection_";
    private static readonly string[] ArrayCollectionKinds=["ICollection","IList","IReadOnlyCollection","IReadOnlyList"];
    [ThreadStatic] private static Dictionary<string,string>? arrayCollectionRequests;
    private static bool ArrayCollectionDerives(IReadOnlyList<ScalarStruct> all,int source,int target)
    {
        if(source<0||source>=all.Count||target<0||target>=all.Count)return false;
        if(all[target].NativeCollectionElement is {} to&&ArrayElementIdentity(all,source)is {} from)
            return from==to||KnownReference(all,from)&&KnownReference(all,to)&&ReferenceConvertible(all,from,to,[]);
        return all[source].NativeCollectionElement is {} element&&all[target].NativeEnumerableElement==element;
    }
    private static string ArrayCollectionSource()
    {
        var text=new StringBuilder();
        foreach(var(key,element)in arrayCollectionRequests??[])
        {
            string Name(string kind)=>NativeCollectionPrefix+kind+"_"+key;
            var collection=Name("ICollection");var list=Name("IList");var readCollection=Name("IReadOnlyCollection");var readList=Name("IReadOnlyList");
            text.Append($"\ninterface {collection}{{public Count: int{{get;}}\npublic IsReadOnly: bool{{get;}}\npublic fn GetEnumerator() -> __IEnumerator_{key}\npublic fn Add(item: {element}) -> void\npublic fn Clear() -> void\npublic fn Remove(item: {element}) -> bool\npublic fn Contains(item: {element}) -> bool\npublic fn CopyTo(array: {element}[],index: int) -> void\n}}\n");
            text.Append($"interface {list}: {collection}{{public this[index: int]: {element}{{get;set;}}\npublic fn Insert(index: int,item: {element}) -> void\npublic fn RemoveAt(index: int) -> void\npublic fn IndexOf(item: {element}) -> int\n}}\n");
            text.Append($"interface {readCollection}{{public Count: int{{get;}}\npublic fn GetEnumerator() -> __IEnumerator_{key}\n}}\ninterface {readList}: {readCollection}{{public this[index: int]: {element}{{get;}}\n}}\n");
            text.Append($"static class {Name("Ops")}{{\n");
            void Method(string name,string parameters,string result,string body)=>text.Append($"public static fn {name}(values: {element}[]{parameters}) -> {result}\n ensures true\n{{{body}}}\n");
            Method("Count","","int","return values.Length");Method("IsReadOnly","","bool","return true");
            const string bounds="if(index<0 || index>=values.Length){throw new ArgumentOutOfRangeException()}\n";
            Method("Get",",index: int",element,bounds+"return values[index]");Method("Set",$",index: int,item: {element}","void",bounds+"values[index]=item");
            Method("Add",$",item: {element}","void","throw new NotSupportedException()");Method("Clear","","void","throw new NotSupportedException()");
            Method("Remove",$",item: {element}","bool","throw new NotSupportedException()");Method("Insert",$",index: int,item: {element}","void","throw new NotSupportedException()");Method("RemoveAt",",index: int","void","throw new NotSupportedException()");
            text.Append("}\n");
        }
        return text.ToString();
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarType? ArrayCollectionTypeText(string text,SourceLocation at,bool forceBuiltin=false)
        {
            var match=Regex.Match(text.Trim(),@"^(ICollection|IList|IReadOnlyCollection|IReadOnlyList)\s*<(.*)>$");
            if(!match.Success)return null;
            var kind=match.Groups[1].Value;var arg=match.Groups[2].Value.Trim();
            // Source declarations take precedence over the implicit library names.
            if(!forceBuiltin&&StructNamedAny(kind)is not null)return null;
            var element=Type(arg,at);if(element==ScalarType.Unit||IsStruct(element)&&StructOf(element).IsStatic)Fail("AI3106","A collection element requires a storage type.",at);
            if(IsTypeParameterType(element)||IsStruct(element)&&StructOf(element).IsCheckInstance)return CheckCollectionType(element,kind,at);
            var syntax=SequenceSyntax(element);var key=SequenceShapeKey(syntax);var name=NativeCollectionPrefix+kind+"_"+key;
            if(StructNamed(name)is not {} found)
            {
                if(arrayCollectionRequests!.Count>=64&&!arrayCollectionRequests.ContainsKey(key))Fail("AI3100","More than 64 array collection shapes are unsupported.",at);
                arrayCollectionRequests.TryAdd(key,syntax);Fail("AI3100","The array collection shape requires another binding pass.",at);return null;
            }
            foreach(var item in ArrayCollectionKinds)if(StructNamed(NativeCollectionPrefix+item+"_"+key)is {} descriptor)
                _structs[IndexOf(descriptor.Id)]=descriptor with{NativeCollectionKind=item,NativeCollectionElement=element};
            if(StructNamed("__IEnumerable_"+key)is {} enumerable)_structs[IndexOf(enumerable.Id)]=enumerable with{NativeEnumerableElement=element};
            return found.Id;
        }
        private ScalarStatement ArrayCollectionDispatch(ScalarStruct owner,ScalarStructMethod root,ScalarExpression receiver,IReadOnlyList<ScalarParameter> parameters,ScalarType result,SourceLocation at)
        {
            var element=owner.NativeCollectionElement!.Value;
            if(root.Name=="GetEnumerator")return ArrayEnumerableDispatch(receiver,element,result,at);
            var key=owner.Name[(NativeCollectionPrefix+owner.NativeCollectionKind+"_").Length..];
            var name=root.IsAccessor?root.AccessorMember switch{"get:Count"=>"Count","get:IsReadOnly"=>"IsReadOnly",_=>root.Name.StartsWith("get_",StringComparison.Ordinal)?"Get":"Set"}:root.Name;
            var view=Type(SequenceSyntax(element)+"[]",at);var array=new ScalarExpression("downcast",view,at,Left:receiver);
            if(root.Name is "Contains" or "IndexOf")return ArrayCollectionSearch(array,ForwardParameter(parameters[0],at),root.Name=="Contains",at);
            if(root.Name=="CopyTo")return new("expression",at,new("array-copy",ScalarType.Unit,at,Left:array,Right:ForwardParameter(parameters[0],at)){Arguments=[ForwardParameter(parameters[1],at)]});
            var call=new ScalarExpression("call",result,at,NativeCollectionPrefix+"Ops_"+key+"."+name){Arguments=[array,..parameters.Select(p=>ForwardParameter(p,at))]};
            return result==ScalarType.Unit?new("expression",at,call):new("return",at,call);
        }
        private ScalarStatement ArrayCollectionSearch(ScalarExpression source,ScalarExpression item,bool contains,SourceLocation at)
        {
            var element=ArrayElement(source.Type);var arrayName=HiddenLocal(source.Type).EmittedName;var indexName=HiddenLocal(ScalarType.Int).EmittedName;var candidateName=HiddenLocal(element).EmittedName;
            var array=new ScalarExpression("local",source.Type,at,arrayName);var index=new ScalarExpression("local",ScalarType.Int,at,indexName);var candidate=new ScalarExpression("local",element,at,candidateName);
            ScalarExpression equal;
            if(IsObject(element)||IsClass(element))
            {
                equal=ReferenceValueEqual(candidate,item,at);
            }
            else if(EquatableValueEqual(new("array-index",element,at,Left:array,Right:index),item,at,liveReceiver:true) is {} typed)equal=typed;
            else if(IsStruct(element)&&StructOf(element) is {} info&&info.IsRecord&&CustomRecordEquality(info)is {} recordEquality)
                equal=InterfaceImplementationCall(recordEquality,new("array-index",element,at,Left:array,Right:index),[item],ScalarType.Bool,at);
            else if(IsStruct(element)&&StructOf(element)is {} valueInfo&&ObjectOverride(valueInfo,ObjectEqualsSignature)is {} valueEquality)
                equal=InterfaceImplementationCall(valueEquality,new("array-index",element,at,Left:array,Right:index),[AsObject(item)],ScalarType.Bool,at);
            else equal=ObjectValueEqual(candidate,item,at);
            var found=contains?new ScalarExpression("literal",ScalarType.Bool,at,"true"):index;
            var absent=new ScalarExpression("literal",contains?ScalarType.Bool:ScalarType.Int,at,contains?"false":"-1");
            var next=new ScalarExpression("=",ScalarType.Int,at,Left:index,Right:new("+",ScalarType.Int,at,Left:index,Right:new("literal",ScalarType.Int,at,"1")));
            return new("block",at,Body:[new("local",at,source,arrayName,source.Type){Mutable=false},new("local",at,new("literal",ScalarType.Int,at,"0"),indexName,ScalarType.Int){Mutable=true},
                new("while",at,new("<",ScalarType.Bool,at,Left:index,Right:new("field",ScalarType.Int,at,"0",Left:array)),Body:[new("local",at,new("array-index",element,at,Left:array,Right:index),candidateName,element){Mutable=true},new("if",at,equal,Body:[new("return",at,found)]),new("expression",at,next)]),new("return",at,absent)]);
        }
    }
}
