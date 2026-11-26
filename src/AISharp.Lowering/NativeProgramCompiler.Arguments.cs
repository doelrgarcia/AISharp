using System.Globalization;
using System.Numerics;
using System.Text;
using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private static bool IsStringArrayType(ScalarType type) => IsClassType(type) && Layout(type).IsStringArray;
    private static bool IsArrayType(ScalarType type) => IsClassType(type) && (Layout(type).IsStringArray || Layout(type).ArrayElementType is not null);
    private static ScalarType ArrayStorageType(ScalarType type) => Layout(type).IsStringArray ? ScalarType.String : BoxStorage(Layout(type).ArrayElementType!.Value);
    private sealed record ArrayNew(ScalarType Kind, Value Count, IReadOnlyList<Value> Elements, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; }
    private sealed record ArrayGet(Value Array, Value Index, SourceLocation At) : Value(At) { public override ScalarType Type => ArrayStorageType(Array.Type); public bool ExactElementAddress { get; init; } }
    private sealed record ArraySet(Value Array, Value Index, Value Element, bool Append, SourceLocation At) : Value(At) { public override ScalarType Type => ArrayStorageType(Array.Type); }
    private sealed record ArrayCopy(Value Source, Value Destination, Value Index, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Unit; }

    private sealed partial class Verifier
    {
        private Value VerifyArrayValue(ScalarExpression e, SourceLocation at)
        {
            if (e.Wraps || e.ReferenceMode != ScalarParameterMode.Value || e.ReferenceExcludedArguments is not null || e.AccessorSetter is not null || e.RuntimeTypeShape is not null || e.RuntimeTypeProof is not null) throw new Rejected(at, "Malformed array operation metadata.");
            if(e.Operation=="array-copy")
            {
                if(e.Type!=ScalarType.Unit||e.Value is not null||e.Left is null||e.Right is null||!IsArrayType(e.Left.Type)||e.Left.Type!=e.Right.Type||e.Arguments is not{Count:1})throw new Rejected(at,"An array copy requires identical logical array types and one destination index.");
                return new ArrayCopy(VerifyValue(e.Left,at),Require(VerifyValue(e.Right,at),e.Left.Type,at),Require(VerifyValue(e.Arguments[0],at),ScalarType.Int,at),at);
            }
            if (e.Operation == "array-new" && e.Value is null && IsArrayType(e.Type) && e.Left is not null && e.Right is null && e.Arguments is { } values)
            {
                var count = Require(VerifyValue(e.Left, at), ScalarType.Int, at);
                if (count is Const { Number.Sign: < 0 }) throw new Rejected(at, "An array length cannot be a negative constant.");
                if (values.Count > 0 && (count is not Const length || length.Number != values.Count)) throw new Rejected(at, "Array initializer length mismatch.");
                return new ArrayNew(e.Type, count, values.Select(v => Require(VerifyValue(v, at), ArrayStorageType(e.Type), v.Location)).ToArray(), at);
            }
            if (e.Left is null || e.Right is null || !IsArrayType(e.Left.Type) || e.Type != ArrayStorageType(e.Left.Type)) throw new Rejected(at, "Expected a typed string array operation.");
            var array = VerifyValue(e.Left, at); var index = Require(VerifyValue(e.Right, at), ScalarType.Int, at);
            if (e.Operation == "array-index" && e.Value is null && e.Arguments is null) return new ArrayGet(array, index, at);
            if (e.Operation == "array-store" && (e.Value == "=" || e.Value == "+=" && IsStringArrayType(e.Left.Type)) && e.Arguments is { Count: 1 } assigned)
                return new ArraySet(array, index, Require(VerifyValue(assigned[0], at), ArrayStorageType(e.Left.Type), assigned[0].Location), e.Value == "+=", at);
            throw new Rejected(at, "Malformed string array operation.");
        }
    }

    private sealed partial class Emitter
    {
        private string ArrayNewHelper(ScalarType type)
        {
            usesObjects = true;
            var ct = CType(type); var element = CType(ArrayStorageType(type)); var tag = ClassTag(type); var name = "ai_array_new_" + tag;
            return Helper(name, $"static {ct}{name}(int32_t n) {{\n if (n < 0) ai_fault(\"System.OverflowException\");\n if ((uint64_t)(uint32_t)n > (uint64_t)AI_ARENA_CAP / sizeof({element})) ai_fault(\"System.OutOfMemoryException\");\n {ct}p = ({ct})ai_arena_alloc(sizeof *p); memset(p, 0, sizeof *p); p->ai_tag = {tag}; p->f0 = n;\n if (n != 0) {{ size_t bytes = (size_t)(uint32_t)n * sizeof({element}); p->ai_items = ({element} *)ai_arena_alloc(bytes); memset(p->ai_items, 0, bytes); }}\n return p;\n}}\n");
        }
        private string ArrayIndexHelper(ScalarType type, bool store)
        {
            if (CovariantArrayCandidates(type).Count > 1) return CovariantArrayIndexHelper(type, store);
            var ct = CType(type); var element = CType(ArrayStorageType(type)); var name = (store ? "ai_array_put_" : "ai_array_at_") + ClassTag(type);
            return Helper(name, $"static {element} {name}({ct}p, int32_t i{(store ? ", " + element + " v" : "")}) {{ if (p == NULL) ai_fault(\"System.NullReferenceException\"); if (i < 0 || i >= p->f0) ai_fault(\"System.IndexOutOfRangeException\"); {(store ? "p->ai_items[i] = v; return v;" : "return p->ai_items[i];")} }}\n");
        }
        private string ArrayAtom(Value value)
        {
            if(value is ArrayCopy copy)
            {
                var source=Atom(copy.Source);var destination=Atom(copy.Destination);var index=Atom(copy.Index);
                c.Append(ArrayCopyHelper(copy.Source.Type)).Append('(').Append(source).Append(", ").Append(destination).Append(", ").Append(index).AppendLine(");");return "0";
            }
            if (value is ArrayNew made)
            {
                var count = Atom(made.Count); var result = Temp();
                c.Append(CType(made.Type)).Append(' ').Append(result).Append(" = ").Append(ArrayNewHelper(made.Type)).Append('(').Append(count).AppendLine(");");
                for (var i = 0; i < made.Elements.Count; i++) { var element = Atom(made.Elements[i]); c.Append(result).Append("->ai_items[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] = ").Append(element).AppendLine(";"); }
                return result;
            }
            if (value is ArrayGet read)
            {
                var array = Atom(read.Array); var index = Atom(read.Index); var result = Temp();
                c.Append(CType(read.Type)).Append(' ').Append(result).Append(" = ").Append(ArrayIndexHelper(read.Array.Type, false)).Append('(').Append(array).Append(", ").Append(index).AppendLine(");"); return result;
            }
            if (value is ArraySet store)
            {
                var array = Atom(store.Array); var index = Atom(store.Index); string? previous = null;
                if (store.Append) { previous = Temp(); c.Append("ai_str ").Append(previous).Append(" = ").Append(ArrayIndexHelper(store.Array.Type, false)).Append('(').Append(array).Append(", ").Append(index).AppendLine(");"); }
                var element = Atom(store.Element);
                if (previous is not null) { usesConcat = true; var combined = Temp(); c.Append("ai_str ").Append(combined).Append(" = ai_str_concat(").Append(previous).Append(", ").Append(element).AppendLine(");"); element = combined; }
                var result = Temp(); c.Append(CType(store.Type)).Append(' ').Append(result).Append(" = ").Append(ArrayIndexHelper(store.Array.Type, true)).Append('(').Append(array).Append(", ").Append(index).Append(", ").Append(element).AppendLine(");"); return result;
            }
            throw new InvalidOperationException("Unverified array value.");
        }
        private string ProcessEntryPrelude(ScalarType arrayType)
        {
            var text = new StringBuilder("int wmain(int argc, wchar_t **argv) { int32_t ai_nargs = argc > 0 ? argc - 1 : 0;\n");
            text.Append(CType(arrayType)).Append(" ai_args = ").Append(ArrayNewHelper(arrayType)).Append("(ai_nargs);\n");
            text.Append("for (int32_t ai_i = 0; ai_i < ai_nargs; ai_i++) { size_t ai_len = wcslen(argv[ai_i + 1]); if (ai_len > INT32_MAX) ai_fault(\"System.OutOfMemoryException\"); ai_args->ai_items[ai_i].p = (const uint16_t *)argv[ai_i + 1]; ai_args->ai_items[ai_i].n = (int32_t)ai_len; }\n");
            return text.ToString();
        }
    }
}
