using System.Text.RegularExpressions;

namespace AISharp.Compiler;

/// <summary>
/// Enforces affine ownership rules for declared AI# resources while leaving
/// ordinary GC-managed values unrestricted.
/// </summary>
public static class AISharpResourceLifetimeAnalyzer
{
    private enum ResourceState
    {
        Available,
        Moved,
        Disposed
    }

    private sealed record ResourceBinding(
        string Name,
        string Type,
        ParameterOwnership Ownership,
        SourceLocation Location);

    private sealed record FunctionContext(
        FunctionDeclaration Function,
        IReadOnlyDictionary<string, FunctionDeclaration> Functions,
        HashSet<string> ResourceTypes,
        IReadOnlyDictionary<string, string> ActorStateTypes,
        Dictionary<string, ResourceBinding> ResourceBindings,
        Dictionary<string, ResourceState> OwnedStates,
        List<Diagnostic> Diagnostics);

    private static readonly Regex s_simpleCallPattern =
        new(@"^(?<name>[A-Za-z_][A-Za-z0-9_#]*)\s*\((?<args>.*)\)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_stateAssignmentPattern =
        new(@"^self\.(?<field>[A-Za-z_][A-Za-z0-9_#]*)\s*=\s*(?<expr>.+)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

    public static IReadOnlyList<Diagnostic> Check(AISharpDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<Diagnostic>();
        var resourceTypes = document.Resources
            .Select(resource => resource.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (resourceTypes.Count == 0)
            return diagnostics;

        var functions = document.Functions
            .Concat(document.Actors.SelectMany(actor => actor.Handlers))
            .GroupBy(fn => fn.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var fn in document.Functions)
            CheckFunction(fn, functions, resourceTypes, actorStateTypes: new Dictionary<string, string>(StringComparer.Ordinal), diagnostics);

        foreach (var actor in document.Actors)
        {
            var actorStateTypes = actor.StateMembers
                .ToDictionary(state => state.Name, state => state.Type, StringComparer.Ordinal);
            foreach (var handler in actor.Handlers)
                CheckFunction(handler, functions, resourceTypes, actorStateTypes, diagnostics);
        }

        return diagnostics;
    }

    private static void CheckFunction(
        FunctionDeclaration fn,
        IReadOnlyDictionary<string, FunctionDeclaration> functions,
        HashSet<string> resourceTypes,
        IReadOnlyDictionary<string, string> actorStateTypes,
        List<Diagnostic> diagnostics)
    {
        var bindings = fn.Parameters
            .Where(parameter => parameter.Ownership is ParameterOwnership.Owned or ParameterOwnership.Borrowed
                && IsDeclaredResourceType(parameter.Type, resourceTypes))
            .ToDictionary(
                parameter => parameter.Name,
                parameter => new ResourceBinding(parameter.Name, parameter.Type, parameter.Ownership, fn.Location),
                StringComparer.Ordinal);

        if (bindings.Count == 0)
            return;

        var ownedStates = bindings.Values
            .Where(binding => binding.Ownership == ParameterOwnership.Owned)
            .ToDictionary(binding => binding.Name, _ => ResourceState.Available, StringComparer.Ordinal);

        var context = new FunctionContext(fn, functions, resourceTypes, actorStateTypes, bindings, ownedStates, diagnostics);
        foreach (var statement in fn.Body.Statements)
            CheckStatement(statement, context);

        foreach (var binding in bindings.Values.Where(binding => binding.Ownership == ParameterOwnership.Owned))
        {
            if (context.OwnedStates.TryGetValue(binding.Name, out var state) && state == ResourceState.Available)
            {
                diagnostics.Add(new Diagnostic(
                    "AI3072",
                    DiagnosticSeverity.Error,
                    $"Owned resource parameter '{binding.Name}' in function '{fn.Name}' must be deterministically disposed with using or transferred exactly once.",
                    fn.Location));
            }
        }
    }

    private static void CheckStatement(FunctionStatement statement, FunctionContext context)
    {
        switch (statement)
        {
            case LetStatement let:
                CheckExpression(let.Initializer.Text, let.Location, context);
                if (let.TypeAnnotation is not null
                    && IsDeclaredResourceType(let.TypeAnnotation, context.ResourceTypes)
                    && TryGetBorrowedBinding(let.Initializer.Text, context, out var borrowed))
                {
                    ReportBorrowEscape(context, borrowed, let.Location, $"borrowed resource '{borrowed.Name}' cannot be stored in resource-typed local '{let.Name}'.");
                }
                break;

            case ReturnStatement ret:
                CheckReturn(ret, context);
                break;

            case ExpressionStatement expression:
                CheckExpressionStatement(expression, context);
                break;

            case TaskGroupStatement taskGroup:
                foreach (var child in taskGroup.Statements)
                    CheckStatement(child, context);
                break;

            case UsingStatement usingStatement:
                CheckUsing(usingStatement, context);
                break;

            case SpawnStatement spawn:
                CheckExpression(spawn.Expression.Text, spawn.Location, context);
                break;
        }
    }

    private static void CheckReturn(ReturnStatement ret, FunctionContext context)
    {
        var expression = ret.Expression.Text.Trim();
        if (context.ResourceBindings.TryGetValue(expression, out var directBinding))
        {
            if (directBinding.Ownership == ParameterOwnership.Borrowed)
            {
                ReportBorrowEscape(context, directBinding, ret.Location, $"borrowed resource '{directBinding.Name}' cannot escape by being returned from function '{context.Function.Name}'.");
                return;
            }

            if (IsDeclaredResourceType(context.Function.ReturnType, context.ResourceTypes))
            {
                MoveOwned(context, directBinding.Name, ret.Location, "returned");
                return;
            }
        }

        CheckExpression(expression, ret.Location, context);
    }

    private static void CheckExpressionStatement(ExpressionStatement statement, FunctionContext context)
    {
        var expression = statement.Expression.Text.Trim();
        var assignment = s_stateAssignmentPattern.Match(expression);
        if (assignment.Success)
        {
            var stateName = assignment.Groups["field"].Value;
            var assignedExpression = assignment.Groups["expr"].Value.Trim();
            if (context.ActorStateTypes.TryGetValue(stateName, out var stateType)
                && IsDeclaredResourceType(stateType, context.ResourceTypes)
                && TryGetBorrowedBinding(assignedExpression, context, out var borrowed))
            {
                ReportBorrowEscape(context, borrowed, statement.Location, $"borrowed resource '{borrowed.Name}' cannot be stored in longer-lived actor state '{stateName}'.");
            }
        }

        CheckExpression(expression, statement.Location, context);
    }

    private static void CheckUsing(UsingStatement usingStatement, FunctionContext context)
    {
        if (!context.ResourceBindings.TryGetValue(usingStatement.ResourceName, out var binding)
            || binding.Ownership != ParameterOwnership.Owned)
        {
            context.Diagnostics.Add(new Diagnostic(
                "AI3073",
                DiagnosticSeverity.Error,
                $"Using statement in function '{context.Function.Name}' must name an owned resource parameter; '{usingStatement.ResourceName}' is not owned by this function.",
                usingStatement.Location));
            foreach (var child in usingStatement.Statements)
                CheckStatement(child, context);
            return;
        }

        if (!EnsureOwnedAvailable(context, usingStatement.ResourceName, usingStatement.Location))
        {
            foreach (var child in usingStatement.Statements)
                CheckStatement(child, context);
            return;
        }

        foreach (var child in usingStatement.Statements)
            CheckStatement(child, context);

        if (context.OwnedStates.TryGetValue(usingStatement.ResourceName, out var state)
            && state == ResourceState.Available)
        {
            context.OwnedStates[usingStatement.ResourceName] = ResourceState.Disposed;
        }
    }

    private static void CheckExpression(string expression, SourceLocation location, FunctionContext context)
    {
        var transferArguments = new HashSet<string>(StringComparer.Ordinal);
        var call = s_simpleCallPattern.Match(expression.Trim());
        if (call.Success)
        {
            var functionName = call.Groups["name"].Value;
            var args = SplitTopLevelArguments(call.Groups["args"].Value);
            if (context.Functions.TryGetValue(functionName, out var callee))
            {
                for (var i = 0; i < Math.Min(args.Count, callee.Parameters.Count); i++)
                {
                    if (CheckArgument(args[i], callee.Parameters[i], functionName, location, context))
                    {
                        transferArguments.Add(args[i].Trim());
                    }
                }
            }
        }

        foreach (var binding in context.ResourceBindings.Values.Where(binding => binding.Ownership == ParameterOwnership.Owned))
        {
            if (transferArguments.Contains(binding.Name))
                continue;

            if (ReferencesIdentifier(expression, binding.Name))
                EnsureOwnedAvailable(context, binding.Name, location);
        }
    }

    private static bool CheckArgument(
        string argument,
        ParameterDeclaration parameter,
        string calleeName,
        SourceLocation location,
        FunctionContext context)
    {
        var trimmedArgument = argument.Trim();
        if (!context.ResourceBindings.TryGetValue(trimmedArgument, out var binding))
            return false;

        if (parameter.Ownership == ParameterOwnership.Owned && IsDeclaredResourceType(parameter.Type, context.ResourceTypes))
        {
            if (binding.Ownership == ParameterOwnership.Borrowed)
            {
                ReportBorrowEscape(context, binding, location, $"borrowed resource '{binding.Name}' cannot be transferred to owned parameter '{parameter.Name}' of function '{calleeName}'.");
                return false;
            }

            MoveOwned(context, binding.Name, location, $"transferred to function '{calleeName}'");
            return true;
        }
        else if (binding.Ownership == ParameterOwnership.Owned)
        {
            EnsureOwnedAvailable(context, binding.Name, location);
        }

        return false;
    }

    private static void MoveOwned(FunctionContext context, string name, SourceLocation location, string action)
    {
        if (!EnsureOwnedAvailable(context, name, location))
            return;

        context.OwnedStates[name] = ResourceState.Moved;
    }

    private static bool EnsureOwnedAvailable(FunctionContext context, string name, SourceLocation location)
    {
        if (!context.OwnedStates.TryGetValue(name, out var state) || state == ResourceState.Available)
            return true;

        var stateText = state == ResourceState.Moved ? "moved" : "disposed";
        context.Diagnostics.Add(new Diagnostic(
            "AI3070",
            DiagnosticSeverity.Error,
            $"Owned resource '{name}' is used after it was {stateText} in function '{context.Function.Name}'.",
            location));
        return false;
    }

    private static bool TryGetBorrowedBinding(string expression, FunctionContext context, out ResourceBinding binding)
    {
        var trimmed = expression.Trim();
        if (context.ResourceBindings.TryGetValue(trimmed, out var direct)
            && direct.Ownership == ParameterOwnership.Borrowed)
        {
            binding = direct;
            return true;
        }

        foreach (var candidate in context.ResourceBindings.Values.Where(candidate => candidate.Ownership == ParameterOwnership.Borrowed))
        {
            if (ReferencesIdentifier(trimmed, candidate.Name))
            {
                binding = candidate;
                return true;
            }
        }

        binding = null!;
        return false;
    }

    private static void ReportBorrowEscape(FunctionContext context, ResourceBinding binding, SourceLocation location, string reason) =>
        context.Diagnostics.Add(new Diagnostic(
            "AI3071",
            DiagnosticSeverity.Error,
            $"{reason} Borrowed resources are limited to the current lexical lifetime.",
            location));

    private static bool IsDeclaredResourceType(string type, HashSet<string> resourceTypes)
    {
        var head = ExtractHeadTypeName(type);
        if (resourceTypes.Contains(head))
            return true;

        if (!type.Contains('<', StringComparison.Ordinal))
            return false;

        return SplitTopLevelArguments(type[(type.IndexOf('<', StringComparison.Ordinal) + 1)..^1])
            .Any(part => IsDeclaredResourceType(part, resourceTypes));
    }

    private static string ExtractHeadTypeName(string type)
    {
        var trimmed = type.Trim();
        var genericStart = trimmed.IndexOf('<', StringComparison.Ordinal);
        return genericStart < 0 ? trimmed : trimmed[..genericStart].Trim();
    }

    private static bool ReferencesIdentifier(string text, string identifier) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_#]){Regex.Escape(identifier)}(?![A-Za-z0-9_#])");

    private static IReadOnlyList<string> SplitTopLevelArguments(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '<' or '(' or '[' => 1,
                '>' or ')' or ']' => -1,
                _ => 0
            };

            if (text[i] == ',' && depth == 0)
            {
                Add(text[start..i]);
                start = i + 1;
            }
        }

        Add(text[start..]);
        return result;

        void Add(string part)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
    }
}
