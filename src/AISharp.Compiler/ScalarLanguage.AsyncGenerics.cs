using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record TaskShape(string Display, string Syntax);
    [ThreadStatic] private static Dictionary<string, TaskShape>? taskShapeRequests;
    [ThreadStatic] private static Dictionary<string, string>? checkTaskNames;
    private const string TaskShapePrefix = "__async_shape_";
    private static string TaskShapeKey(string text) => Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$") && !text.StartsWith(TaskShapePrefix, StringComparison.Ordinal)
        ? text : TaskShapePrefix + Convert.ToHexString(Encoding.UTF8.GetBytes(text));
    private static string TaskShapeDisplay(string name, int prefix) => checkTaskNames?.TryGetValue(name, out var check) == true ? check
        : (prefix == 7 ? "Task<" : "TaskCompletionSource<") + (taskShapeRequests?.TryGetValue(name[prefix..], out var shape) == true ? shape.Display : name[prefix..]) + ">";

    private sealed partial class ExtendedBinder
    {
        private int _asyncTypeDepth;
        private string TaskElementText(ScalarType type, bool display)
        {
            if (IsEnum(type)) return display?EnumOf(type).Name:TypeIdentifierSyntax(EnumOf(type).Name);
            if (IsObject(type)) return "object";
            if (!IsStruct(type)) return type.ToString().ToLowerInvariant();
            var info = StructOf(type);
            if (info.IsStringArray) return display ? "string[]" : info.Name;
            if (info.ArrayElementType is { } element) return TaskElementText(element, display) + "[]";
            if (info.Template >= 0)
                return (display?_structs[info.Template].Name:TypeIdentifierSyntax(_structs[info.Template].Name)) + "<" + string.Join(", ", info.TypeArgumentList.Select(t => TaskElementText(t, display))) + ">";
            if (display && IsDelegate(type)) return DelegateOf(type).Name;
            return display ? DisplayName(info.Name) : TypeIdentifierSyntax(info.Name);
        }
        private ScalarType ResolvedTaskType(ScalarType element, bool completionSource, SourceLocation at)
        {
            if (element == ScalarType.Unit) Fail("AI3107", "A task result cannot be void or unit.", at);
            if (IsNull(element) || IsFunctionValue(element)) Fail("AI3116", "The task result type cannot be inferred from this value.", at);
            if (IsTypeParameterType(element) || IsStruct(element) && StructOf(element).IsCheckInstance)
                return CheckTaskType(element, completionSource, at);
            var key = TaskShapeKey(TaskElementText(element, true));
            var task = StructNamed("__Task_" + key);
            if (task?.Fields.FirstOrDefault(f => f.Name == "result") is { } typed && typed.Type != element) task = null;
            task ??= _structs.FirstOrDefault(s => IsTaskType(s.Id) && s.Name.StartsWith("__Task_", StringComparison.Ordinal)
                && s.Fields.Any(f => f.Name == "result" && f.Type == element) && StructNamed("__TCS_" + s.Name[7..]) is not null);
            if (task is null)
            {
                if (taskShapeRequests!.Count >= 256 && !taskShapeRequests.ContainsKey(key)) Fail("AI3100", "More than 256 task result shapes are not supported.", at);
                taskShapeRequests.TryAdd(key, new(TaskElementText(element, true), TaskElementText(element, false)));
                Fail("AI3100", "The task result shape requires another binding pass.", at);
            }
            if (!completionSource) return task!.Id;
            if (StructNamed("__TCS_" + task!.Name[7..]) is { } source) return source.Id;
            Fail("AI3106", "The completion-source shape is unavailable.", at); return ScalarType.Unit;
        }
        private ScalarType CheckTaskType(ScalarType element, bool completionSource, SourceLocation at)
        {
            var key = "__check_" + ((int)element).ToString(CultureInfo.InvariantCulture);
            string Unique(string name) { while (_structs.Any(s => s.Name == name)) name += "_"; return name; }
            // Source classes can use these spellings. Reuse only compiler-owned descriptors,
            // and allocate a fresh spelling when a source declaration occupies the name.
            var task = _structs.FirstOrDefault(s => s.IsCheckInstance && s.Template < 0 && s.Name.StartsWith("__Task___check_", StringComparison.Ordinal)
                && s.Fields.Any(f => f.Name == "result" && f.Type == element));
            var taskName = task?.Name ?? Unique("__Task_" + key);
            if (task is null)
            {
                if (_structs.Count >= 1000) Fail("AI3100", "Too many types.", at);
                var index = _structs.Count; var type = (ScalarType)(StructTypeBase + index); var root = StructNamed("__Task")!;
                var inherited = templateMembersReady ? root.Fields : [];
                _structs.Add(new ScalarStruct(taskName, type, [.. inherited, new ScalarStructField("result", element, EraseType(element), true, false, at) { Owner = index }], [], at, IsClass: true)
                    { BaseIndex = IndexOf(root.Id), IsCheckInstance = true });
                checkTaskNames![taskName] = "Task<" + TaskElementText(element, true) + ">"; task = _structs[index];
            }
            if (!completionSource) return task.Id;
            var existing = _structs.FirstOrDefault(s => s.IsCheckInstance && s.Template < 0 && s.Name.StartsWith("__TCS___check_", StringComparison.Ordinal)
                && s.Fields.Any(f => f.Name == "Task" && f.Type == task.Id));
            if (existing is not null) return existing.Id;
            var name = Unique("__TCS_" + task.Name[7..]);
            if (_structs.Count >= 1000) Fail("AI3100", "Too many types.", at);
            var owner = _structs.Count; var id = (ScalarType)(StructTypeBase + owner);
            ScalarStructMethod Method(string member, string result, IReadOnlyList<ParameterDeclaration> parameters, bool constructor = false) =>
                new(member, name + "." + member, true, constructor, new FunctionDeclaration(member, parameters, result, [], [], new FunctionBody([]) { ScalarSource = new("", at) }, [], at)) { Owner = owner };
            _structs.Add(new ScalarStruct(name, id, [new ScalarStructField("Task", task.Id, task.Id, true, false, at) { Owner = owner }],
                [Method("new", name, [], true), Method("SetResult", "void", [new("v", "__task_element")]), Method("TrySetResult", "bool", [new("v", "__task_element")])], at, IsClass: true)
                { IsCheckInstance = true, TypeArguments = new Dictionary<string, ScalarType>(StringComparer.Ordinal) { ["__task_element"] = element } });
            checkTaskNames![name] = "TaskCompletionSource<" + TaskElementText(element, true) + ">";
            return id;
        }
    }
}
