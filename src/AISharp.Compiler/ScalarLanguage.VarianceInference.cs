namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private bool UsesVariantInference(FunctionDeclaration declaration)
        {
            var pending = new Stack<string>(declaration.Parameters.Select(p => ParameterPassing(p.Type).Element));
            while (pending.Count > 0)
            {
                var text = pending.Pop();
                if (Has(ScalarFeatures.TypedArrays) && text.TrimEnd().EndsWith("[]", StringComparison.Ordinal)) return true;
                var open = text.IndexOf('<'); if (open < 0 || !text.EndsWith('>')) continue;
                var head=GenericLibraryTypeHead(text[..open].Trim());
                if(BuiltinSequenceTypeName(head)&&head is "IEnumerable" or "IEnumerator" or "IReadOnlyList" or "IReadOnlyCollection")return true;
                var type = StructNamedAny(head);
                if (type is not null && (type.IsInterface || type.IsDelegate) && type.TypeVariances.Any(v => v != 0)) return true;
                foreach (var argument in SplitTypeList(text[(open + 1)..^1])) pending.Push(argument);
            }
            return false;
        }

        private ScalarType? InferVariantArgument(FunctionDeclaration declaration, string parameter, IReadOnlyList<ScalarExpression> values)
        {
            var bounds = new List<(ScalarType Type, int Mode)>();
            IReadOnlyList<string> FormalMatches(string initial, string target)
            {
                var matches = new HashSet<string>(); var seen = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(initial);
                while (pending.Count > 0)
                {
                    var text = pending.Pop().Trim(); if (!seen.Add(text)) continue;
                    var open = text.IndexOf('<'); var head = open < 0 ? text : text[..open].Trim();
                    if (head == target) { matches.Add(text); continue; }
                    var type = StructNamedAny(head); if (type is null) continue;
                    var args = open < 0 ? [] : SplitTypeList(text[(open + 1)..^1]).ToArray();
                    var map = type.TypeParameters.Zip(args).ToDictionary(p => p.First, p => p.Second);
                    foreach (var parent in genericBases!.GetValueOrDefault(IndexOf(type.Id), []))
                        pending.Push(RewriteTypeIdentifiers(parent.Name,name=>map.GetValueOrDefault(name)));
                }
                return matches.ToArray();
            }
            void Collect(string initial, ScalarType initialType)
            {
                var seen = new HashSet<(string, ScalarType, int)>(); var pending = new Stack<(string Text, ScalarType Type, int Mode)>(); var passing = ParameterPassing(initial); pending.Push((passing.Element, initialType, passing.Mode == ScalarParameterMode.Value ? 1 : 0));
                while (pending.Count > 0)
                {
                    var (text, actual, mode) = pending.Pop(); var formal = text.Trim();
                    if (IsNull(actual) || !seen.Add((formal, actual, mode))) continue;
                    if (TypeParameterReference(formal,parameter)) { bounds.Add((actual, mode)); continue; }
                    if (Has(ScalarFeatures.TypedArrays) && formal.EndsWith("[]", StringComparison.Ordinal) && IsArray(actual))
                    {
                        var element = ArrayElement(actual);
                        pending.Push((formal[..^2].Trim(), element, mode == 0 || !KnownReference(_structs, element) ? 0 : mode));
                        continue;
                    }
                    var open = formal.IndexOf('<'); if (open < 0 || !formal.EndsWith('>') || !IsStruct(actual)) continue;
                    var head = GenericLibraryTypeHead(formal[..open].Trim()); var arguments = SplitTypeList(formal[(open + 1)..^1]).ToArray(); var info = StructOf(actual);
                    if (head == "Task" && IsTaskType(actual) && arguments.Length == 1 && TaskElement(actual) != ScalarType.Unit)
                    { pending.Push((arguments[0], TaskElement(actual), 0)); continue; }
                    if (head == "TaskCompletionSource" && arguments.Length == 1 && info.Name.StartsWith("__TCS_", StringComparison.Ordinal) && info.Fields.FirstOrDefault(f => f.Name == "Task") is { } task)
                    { pending.Push((arguments[0], TaskElement(task.Type), 0)); continue; }
                    if(arguments.Length==1&&InferredSequenceElement(formal[..open].Trim(),actual) is {} sequenceElement)
                    {
                        var variance=head is "IEnumerable" or "IEnumerator" or "IReadOnlyList" or "IReadOnlyCollection";
                        pending.Push((arguments[0],sequenceElement,mode==0||!variance||!KnownReference(_structs,sequenceElement)?0:mode));continue;
                    }
                    if (info.Template < 0 || _structs[info.Template].Name != head)
                    {
                        if (mode > 0)
                        {
                            var matches = new HashSet<int>(); var visited = new HashSet<int>(); var types = new Stack<int>(); types.Push(IndexOf(actual));
                            while (types.Count > 0)
                            {
                                var next = types.Pop(); if (!visited.Add(next)) continue; var type = _structs[next];
                                if (type.Template >= 0 && _structs[type.Template].Name == head) matches.Add(next);
                                if (type.BaseIndex >= 0) types.Push(type.BaseIndex);
                                foreach (var contract in type.Interfaces) types.Push(contract);
                            }
                            if (matches.Count == 1) pending.Push((formal, _structs[matches.Single()].Id, mode));
                        }
                        else if (mode < 0 && info.Template >= 0)
                        {
                            var matches = FormalMatches(formal, _structs[info.Template].Name);
                            if (matches.Count == 1) pending.Push((matches[0], actual, mode));
                        }
                        continue;
                    }
                    if (arguments.Length != info.TypeArgumentList.Count) continue;
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        var variance = (info.IsInterface || info.IsDelegate) && i < info.TypeVariances.Count ? info.TypeVariances[i] : 0;
                        var next = mode == 0 || variance == 0 || !KnownReference(_structs, info.TypeArgumentList[i]) ? 0 : mode * variance;
                        pending.Push((arguments[i], info.TypeArgumentList[i], next));
                    }
                }
            }
            for (var i = 0; i < values.Count; i++) if (values[i].Type != ScalarType.Unit) Collect(declaration.Parameters[i].Type, values[i].Type);
            bool Converts(ScalarType from, ScalarType to) => from == to
                || ScalarConstants.ImplicitNumeric(from, to) || ParameterConstraintConversion(from, to);
            var candidates = bounds.Select(b => b.Type).Distinct().Where(t => t != ScalarType.Unit && !IsFunctionValue(t)
                && bounds.All(b => b.Mode == 0 ? b.Type == t : b.Mode > 0 ? Converts(b.Type, t) : Converts(t, b.Type))).ToArray();
            var best = candidates.Where(t => candidates.All(other => Converts(other, t))).ToArray();
            return best.Length == 1 ? best[0] : null;
        }
    }
}
