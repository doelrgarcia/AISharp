using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeSequenceShapePrefix="__native_sequence_shape_";
    [ThreadStatic] private static Dictionary<string,string>? iteratorShapeRequests;

    // A source spelling for the exact closed identity, including native aliases hidden by source types.
    public static string SequenceElementSyntax(IReadOnlyList<ScalarStruct> types,IReadOnlyList<ScalarEnum> enums,ScalarType type,int depth=0)
    {
        if(depth>=64)throw new InvalidOperationException("Sequence element descriptions exceed 64 levels.");
        if((int)type==ObjectTypeId)return "object";
        if(enums.FirstOrDefault(e=>e.Id==type) is {} enumeration)return TypeIdentifierSyntax(enumeration.Name);
        var index=(int)type-StructTypeBase;
        if(index<0||index>=types.Count)
        {
            if(!ScalarConstants.IsInteger(type)&&!ScalarConstants.IsFloat(type)&&type is not (ScalarType.Bool or ScalarType.Char or ScalarType.String or ScalarType.Decimal))throw new InvalidOperationException("Unknown sequence element identity.");
            var scalar=ScalarConstants.Spelling(type);
            if(ScalarConstants.IsNative(type)&&types.Any(t=>t.Name==scalar&&!t.IsTypeParameter&&t.TypeParameters.Count==0))
                return type==ScalarType.NInt?"global::System.IntPtr":"global::System.UIntPtr";
            return scalar;
        }
        var owner=types[index];
        if(owner.Id!=type||owner.Template>=types.Count)throw new InvalidOperationException("Invalid sequence element descriptor.");
        if(owner.IsStringArray)return owner.Name;
        if(owner.ArrayElementType is {} element)return SequenceElementSyntax(types,enums,element,depth+1)+"[]";
        if(owner.Template>=0)
            return TypeIdentifierSyntax(types[owner.Template].Name)+"<"+string.Join(", ",owner.TypeArgumentList.Select(t=>SequenceElementSyntax(types,enums,t,depth+1)))+">";
        return TypeIdentifierSyntax(owner.Name);
    }

    private static string SequenceShapeKey(string syntax)=>Regex.IsMatch(syntax,@"^[A-Za-z_][A-Za-z0-9_]*$")&&!syntax.StartsWith(NativeSequenceShapePrefix,StringComparison.Ordinal)
        ?syntax:NativeSequenceShapePrefix+Convert.ToHexString(Encoding.UTF8.GetBytes(syntax));

    private sealed partial class ExtendedBinder
    {
        private bool BuiltinSequenceTypeName(string name)=>Has(ScalarFeatures.Iterators)&&name is "IEnumerable" or "IEnumerator" || Has(ScalarFeatures.TypedArrays)&&ArrayCollectionKinds.Contains(name);
        private string GenericLibraryTypeHead(string name)
        {
            if(QualifiedBuiltinAlias(name) is {} alias)return alias;
            if(StructNamedAny(name) is not null)return name;
            return name switch{"IEquatable"=>NativeEquatableName,"EqualityComparer"=>NativeEqualityComparerName,"IEqualityComparer"=>NativeEqualityComparerInterface,_=>name};
        }
        private ScalarType? InferredSequenceElement(string written,ScalarType actual)
        {
            var kind=QualifiedBuiltinAlias(written)??written;
            if(!BuiltinSequenceTypeName(kind)||QualifiedBuiltinAlias(written)is null&&StructNamedAny(written)is not null||!IsStruct(actual))return null;
            if(kind!="IEnumerator"&&IsArray(actual))return ArrayElement(actual);
            var elements=new HashSet<ScalarType>();var pending=new Stack<int>();var seen=new HashSet<int>();pending.Push(IndexOf(actual));
            while(pending.Count>0)
            {
                var index=pending.Pop();if(!seen.Add(index))continue;var owner=_structs[index];
                if(owner.NativeCollectionElement is {} element&&owner.NativeCollectionKind is {} source
                    &&(kind==source||kind=="IEnumerable"||kind=="ICollection"&&source=="IList"||kind=="IReadOnlyCollection"&&source=="IReadOnlyList"))elements.Add(element);
                if(IsIteratorInterface(owner.Name)&&IteratorShape(owner.Id) is {} iterator&&kind==(iterator.Sequence?"IEnumerable":"IEnumerator"))elements.Add(iterator.Element);
                if(owner.BaseIndex>=0)pending.Push(owner.BaseIndex);
                foreach(var contract in owner.Interfaces)pending.Push(contract);
            }
            return elements.Count==1?elements.Single():null;
        }
        private string SequenceSyntax(ScalarType element)=>SequenceElementSyntax(_structs,_enums,element);
        private string DescriptorArgumentSyntax(ScalarType element)=>IsTypeParameterType(element)||IsStruct(element)&&StructOf(element).IsCheckInstance?SignatureTypeName(element):SequenceSyntax(element);
        private static string CheckSequenceKey(ScalarType element)=>NativeSequenceShapePrefix+"check_"+((int)element).ToString(CultureInfo.InvariantCulture);
        private ScalarStructMethod CheckSequenceMethod(string owner,int index,string name,string result,IReadOnlyList<ParameterDeclaration> parameters,string? accessor=null)
            =>new(name,owner+"."+name,true,false,new(name,parameters,result,[],[],new FunctionBody([]),[],Current.Location))
            {Owner=index,IsAbstract=true,Slot=owner+"."+name,IsAccessor=accessor is not null,AccessorMember=accessor};

        private ScalarType CheckIteratorType(ScalarType element,bool sequence,SourceLocation at)
        {
            var key=CheckSequenceKey(element);var enumerable="__IEnumerable_"+key;var enumerator="__IEnumerator_"+key;
            if(StructNamed(sequence?enumerable:enumerator) is {} found)return found.Id;
            if(_structs.Count+2>=1000)Fail("AI3100","Too many sequence descriptors.",at);
            var index=_structs.Count;var id=(ScalarType)(StructTypeBase+index);
            _structs.Add(new(enumerator,id,[],[
                CheckSequenceMethod(enumerator,index,"MoveNext","bool",[]),
                CheckSequenceMethod(enumerator,index,"get_Current",SignatureTypeName(element),[]),
                CheckSequenceMethod(enumerator,index,"Dispose","void",[])],at,IsClass:true){IsAbstract=true,IsCheckInstance=true});
            activeIterators!.Add(enumerator);
            index=_structs.Count;var sequenceId=(ScalarType)(StructTypeBase+index);
            _structs.Add(new(enumerable,sequenceId,[],[CheckSequenceMethod(enumerable,index,"GetEnumerator",enumerator,[])],at,IsClass:true)
                {IsAbstract=true,IsCheckInstance=true,NativeEnumerableElement=element});
            activeIterators.Add(enumerable);
            return sequence?sequenceId:id;
        }

        private ScalarType CheckCollectionType(ScalarType element,string kind,SourceLocation at)
        {
            var name=NativeCollectionPrefix+"check_"+kind+"_"+((int)element).ToString(CultureInfo.InvariantCulture);
            if(StructNamed(name) is {} found)return found.Id;
            var enumerator=CheckIteratorType(element,false,at);var syntax=SignatureTypeName(element);
            var parent=kind=="IList"?CheckCollectionType(element,"ICollection",at):kind=="IReadOnlyList"?CheckCollectionType(element,"IReadOnlyCollection",at):(ScalarType?)null;
            if(_structs.Count>=1000)Fail("AI3100","Too many collection descriptors.",at);
            var index=_structs.Count;var id=(ScalarType)(StructTypeBase+index);var methods=new List<ScalarStructMethod>();var properties=new List<ScalarProperty>();
            void M(string method,string result,IReadOnlyList<ParameterDeclaration> parameters,string? accessor=null)=>methods.Add(CheckSequenceMethod(name,index,method,result,parameters,accessor));
            void P(string member,ScalarType type,string text,string getter,string? setter=null)
            {
                properties.Add(new(member,type,text,true,true,name+"."+getter,setter is not null,setter is null?null:name+"."+setter,false,null,at)
                    {Owner=index,IsIndexer=member=="this",IndexParameters=member=="this"?[new("index","int")]:[]});
            }
            if(kind is "ICollection" or "IReadOnlyCollection")
            {M("get_Count","int",[],"get:Count");P("Count",ScalarType.Int,"int","get_Count");M("GetEnumerator",StructOf(enumerator).Name,[]);}
            if(kind=="ICollection")
            {
                M("get_IsReadOnly","bool",[],"get:IsReadOnly");P("IsReadOnly",ScalarType.Bool,"bool","get_IsReadOnly");
                M("Add","void",[new("item",syntax)]);M("Clear","void",[]);M("Remove","bool",[new("item",syntax)]);M("Contains","bool",[new("item",syntax)]);M("CopyTo","void",[new("array",syntax+"[]"),new("index","int")]);
            }
            if(kind is "IList" or "IReadOnlyList")
            {
                M("get_this__indexer_0",syntax,[new("index","int")],"get:this");
                if(kind=="IList")M("set_this__indexer_0","unit",[new("index","int"),new("value",syntax)],"set:this");
                P("this",element,syntax,"get_this__indexer_0",kind=="IList"?"set_this__indexer_0":null);
            }
            if(kind=="IList"){M("Insert","void",[new("index","int"),new("item",syntax)]);M("RemoveAt","void",[new("index","int")]);M("IndexOf","int",[new("item",syntax)]);}
            _structs.Add(new(name,id,[],methods,at,IsClass:true){IsInterface=true,IsCheckInstance=true,Properties=properties,Interfaces=parent is {} p?[IndexOf(p)]:[],NativeCollectionKind=kind,NativeCollectionElement=element});
            return id;
        }
    }
}
