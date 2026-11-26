using System.Globalization;
using System.Text;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    public const string NativeArrayEnumeratorPrefix = "__native_enumerator_";
    [ThreadStatic] private static Dictionary<string, string>? arrayEnumeratorRequests;

    private static bool ArrayEnumerableDerives(IReadOnlyList<ScalarStruct> all, int source, int target)
    {
        if (source < 0 || source >= all.Count || target < 0 || target >= all.Count
            || ArrayElementIdentity(all, source) is not { } from || all[target].NativeEnumerableElement is not { } to) return false;
        return from == to || KnownReference(all, from) && KnownReference(all, to) && ReferenceConvertible(all, from, to, []);
    }

    private static string ArrayEnumeratorSource()
    {
        var text = new StringBuilder();
        foreach (var (key, element) in arrayEnumeratorRequests ?? [])
        {
            var name = NativeArrayEnumeratorPrefix + key;
            text.Append($"\nsealed class {name} : __IEnumerator_{key} {{\nprivate source: {element}[]\nprivate position: int\n");
            text.Append($"private static empty: {name}\npublic static fn Create(source: {element}[]) -> __IEnumerator_{key}\n ensures true\n{{if(source.Length==0){{if(empty==null){{empty=new {name}(source)}}\nreturn empty}}\nreturn new {name}(source)}}\n");
            text.Append($"public fn new(source: {element}[]) -> {name}\n ensures true\n{{this.source=source\nthis.position=-1}}\n");
            text.Append("public override fn MoveNext() -> bool\n ensures true\n{if(position<source.Length){position++}\nreturn position<source.Length}\n");
            text.Append($"public override fn get_Current() -> {element}\n ensures true\n{{if(position<0 || position>=source.Length){{throw new InvalidOperationException()}}\nreturn source[position]}}\n");
            text.Append("public override fn Dispose() -> void\n ensures true\n{}\n}\n");
        }
        return text.ToString();
    }

    private sealed partial class ExtendedBinder
    {
        private ScalarStatement ArrayEnumerableDispatch(ScalarExpression receiver, ScalarType element, ScalarType result, SourceLocation at)
        {
            var syntax = SequenceSyntax(element);
            var key = StructOf(result).Name[14..];
            var name = NativeArrayEnumeratorPrefix + key;
            if (StructNamed(name) is not { } iterator)
            {
                if (arrayEnumeratorRequests!.Count >= 128 && !arrayEnumeratorRequests.ContainsKey(key)) Fail("AI3100", "More than 128 array enumerator shapes are unsupported.", at);
                arrayEnumeratorRequests.TryAdd(key, syntax);
                Fail("AI3100", "The array enumerator shape requires another binding pass.", at);
                return null!;
            }
            var view = Type(syntax + "[]", at);
            var array = new ScalarExpression("downcast", view, at, Left: receiver);
            var made = new ScalarExpression("call", result, at, iterator.Name + ".Create") { Arguments = [array] };
            return new("return", at, made);
        }
    }
}
