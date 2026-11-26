namespace AISharp.Compiler;

// ── Type model ────────────────────────────────────────────────────────────────

/// <summary>Base type for all AI# first-class type representations.</summary>
public abstract record AISharpType
{
    /// <summary>Sentinel: type could not be determined; suppress further errors.</summary>
    public static readonly AISharpType Unknown = new UnknownAIType();

    /// <summary>Sentinel: error/error-recovery type.</summary>
    public static readonly AISharpType Error = new ErrorAIType();
}

/// <summary>One of the built-in primitive types (string, int, bool, …).</summary>
public sealed record PrimitiveAIType(string Name) : AISharpType;

/// <summary>A named (non-generic) type – may or may not be resolved to a symbol.</summary>
public sealed record NamedAIType(string Name, Symbol? ResolvedSymbol) : AISharpType;

/// <summary>A generic type application: head type plus (possibly empty) type arguments.</summary>
public sealed record GenericAIType(string Head, IReadOnlyList<AISharpType> Args) : AISharpType;

/// <summary>A function type with ordered parameter types and a return type.</summary>
public sealed record FunctionAIType(IReadOnlyList<AISharpType> Parameters, AISharpType ReturnType) : AISharpType;

/// <summary>Result carrier with success and error payload types.</summary>
public sealed record ResultAIType(AISharpType OkType, AISharpType ErrorType) : AISharpType;

/// <summary>Option carrier with a present-value payload type.</summary>
public sealed record OptionAIType(AISharpType ValueType) : AISharpType;

/// <summary>A resource type declared with the <c>resource</c> keyword.</summary>
public sealed record ResourceAIType(string Name) : AISharpType;

/// <summary>A union type whose cases are listed by name.</summary>
public sealed record UnionAIType(IReadOnlyList<string> Cases, string? Name = null) : AISharpType;

/// <summary>A record type with named fields.</summary>
public sealed record RecordAIType(string Name, IReadOnlyList<ParameterDeclaration> Fields) : AISharpType;

/// <summary>Type that could not be determined; further type errors are suppressed.</summary>
public sealed record UnknownAIType() : AISharpType;

/// <summary>Error / error-recovery type.</summary>
public sealed record ErrorAIType() : AISharpType;

// ── Type checker ──────────────────────────────────────────────────────────────

/// <summary>
/// First-pass type checker for AI# function bodies.
/// Detects return-type mismatches (AI3020), let-annotation mismatches (AI3021),
/// and undeclared model-call output schemas (AI3022).
/// Preserves partial typing: when an expression type cannot be inferred, the check
/// is silently skipped rather than producing a false positive.
/// </summary>
public static class AISharpTypeChecker
{
    private sealed record TypeContext(
        SymbolTable SymbolTable,
        IReadOnlyDictionary<string, AISharpType> DeclaredTypes,
        IReadOnlyDictionary<string, FunctionAIType> Functions);

    private static readonly HashSet<string> s_primitiveNames = new(StringComparer.Ordinal)
    {
        "string", "int", "bool", "float", "bytes", "unit", "void",
        "byte", "sbyte", "short", "ushort", "uint", "long", "ulong", "double", "decimal",
        "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64",
        "f32", "f64", "char", "any", "never", "unknown", "Error",
        "CustomerId", "ApiKey", "OrderId"
    };

    private static readonly HashSet<string> s_wellKnownCarriers = new(StringComparer.Ordinal)
    {
        "Result", "Option", "List", "Map", "Set", "Tuple",
        "Stream", "Future", "Task", "Array", "Dict", "Pair",
        "Either", "Vec"
    };

    /// <summary>
    /// Type-checks all function (and actor handler) bodies in <paramref name="document"/>.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Check(
        AISharpDocument document,
        SymbolTable symbolTable)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(symbolTable);

        var diagnostics = new List<Diagnostic>();
        var context = BuildTypeContext(document, symbolTable);
        var unionTypes = document.Types
            .Where(t => t.Kind == TypeKind.Union)
            .ToDictionary(t => t.Name, t => (IReadOnlyList<string>)t.Cases, StringComparer.Ordinal);

        foreach (var fn in document.Functions)
        {
            CheckFunction(fn, context, unionTypes, diagnostics);
            CheckModelCallOutputSchemas(fn, symbolTable, diagnostics);
        }

        foreach (var actor in document.Actors)
            foreach (var handler in actor.Handlers)
            {
                CheckFunction(handler, context, unionTypes, diagnostics);
                CheckModelCallOutputSchemas(handler, symbolTable, diagnostics);
            }

        return diagnostics;
    }

    private static TypeContext BuildTypeContext(AISharpDocument document, SymbolTable symbolTable)
    {
        var declaredTypes = new Dictionary<string, AISharpType>(StringComparer.Ordinal);

        foreach (var type in document.Types)
        {
            declaredTypes[type.Name] = type.Kind switch
            {
                TypeKind.Record => new RecordAIType(type.Name, type.Fields),
                TypeKind.Union => new UnionAIType(type.Cases, type.Name),
                _ => new NamedAIType(type.Name, symbolTable.LookupDocument(SymbolKind.Type, type.Name))
            };
        }

        foreach (var schema in document.Schemas)
            declaredTypes[schema.Name] = new RecordAIType(schema.Name, schema.Fields);

        foreach (var resource in document.Resources)
            declaredTypes[resource.Name] = new ResourceAIType(resource.Name);

        var functions = new Dictionary<string, FunctionAIType>(StringComparer.Ordinal);
        foreach (var fn in document.Functions)
            functions[fn.Name] = BuildFunctionType(fn, symbolTable, declaredTypes);

        foreach (var actor in document.Actors)
            foreach (var handler in actor.Handlers)
                functions[handler.Name] = BuildFunctionType(handler, symbolTable, declaredTypes);

        return new TypeContext(symbolTable, declaredTypes, functions);
    }

    private static FunctionAIType BuildFunctionType(
        FunctionDeclaration fn,
        SymbolTable symbolTable,
        IReadOnlyDictionary<string, AISharpType> declaredTypes)
    {
        var parameters = fn.Parameters
            .Select(parameter => ParseType(parameter.Type, symbolTable, declaredTypes))
            .ToList();
        var returnType = ParseType(fn.ReturnType, symbolTable, declaredTypes);
        return new FunctionAIType(parameters, returnType);
    }

    private static void CheckFunction(
        FunctionDeclaration fn,
        TypeContext context,
        Dictionary<string, IReadOnlyList<string>> unionTypes,
        List<Diagnostic> diagnostics)
    {
        var returnType = ParseType(fn.ReturnType, context.SymbolTable, context.DeclaredTypes);
        var env = new Dictionary<string, AISharpType>(StringComparer.Ordinal);
        foreach (var param in fn.Parameters)
            env[param.Name] = ParseType(param.Type, context.SymbolTable, context.DeclaredTypes);

        foreach (var stmt in fn.Body.Statements)
            CheckStatement(stmt, fn.Name, returnType, env, context, unionTypes, diagnostics);
    }

    private static void CheckStatement(
        FunctionStatement stmt,
        string fnName,
        AISharpType returnType,
        Dictionary<string, AISharpType> env,
        TypeContext context,
        Dictionary<string, IReadOnlyList<string>> unionTypes,
        List<Diagnostic> diagnostics)
    {
        switch (stmt)
        {
            case ReturnStatement ret:
                CheckReturn(ret, fnName, returnType, env, context, diagnostics);
                break;

            case LetStatement let:
                CheckLet(let, env, context, diagnostics);
                break;

            case TaskGroupStatement tg:
                foreach (var inner in tg.Statements)
                    CheckStatement(inner, fnName, returnType, env, context, unionTypes, diagnostics);
                break;

            case MatchStatement match:
                CheckMatch(match, fnName, env, context, unionTypes, diagnostics);
                break;
        }
    }

    private static void CheckMatch(
        MatchStatement match,
        string fnName,
        Dictionary<string, AISharpType> env,
        TypeContext context,
        Dictionary<string, IReadOnlyList<string>> unionTypes,
        List<Diagnostic> diagnostics)
    {
        var scrutinee = match.Scrutinee.Trim();
        if (!env.TryGetValue(scrutinee, out var scrutineeType))
            return; // Can't determine scrutinee type — skip exhaustiveness check

        string? typeName = scrutineeType switch
        {
            NamedAIType n => n.Name,
            UnionAIType u when u.Name is not null => u.Name,
            UnionAIType u => unionTypes.FirstOrDefault(kv => kv.Value.SequenceEqual(u.Cases, StringComparer.Ordinal)).Key,
            _ => null
        };

        if (typeName is null || !unionTypes.TryGetValue(typeName, out var cases))
            return; // Not a known union type — skip

        var caseSet = cases.ToHashSet(StringComparer.Ordinal);
        var coveredPatterns = new HashSet<string>(StringComparer.Ordinal);
        var wildcardSeen = false;
        AISharpType? expectedArmType = null;

        foreach (var arm in match.Arms)
        {
            if (wildcardSeen)
            {
                diagnostics.Add(new Diagnostic(
                    "AI3061",
                    DiagnosticSeverity.Error,
                    $"Match arm '{arm.Pattern}' in '{fnName}' is unreachable because an earlier wildcard arm covers all remaining cases.",
                    arm.Location));
            }

            if (arm.Pattern == "_")
            {
                if (!coveredPatterns.Add(arm.Pattern))
                {
                    diagnostics.Add(new Diagnostic(
                        "AI3061",
                        DiagnosticSeverity.Error,
                        $"Match arm '_' in '{fnName}' is declared more than once.",
                        arm.Location));
                }

                wildcardSeen = true;
            }
            else if (!caseSet.Contains(arm.Pattern))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3061",
                    DiagnosticSeverity.Error,
                    $"Match arm '{arm.Pattern}' in '{fnName}' is unreachable because '{arm.Pattern}' is not a case of union type '{typeName}'.",
                    arm.Location));
            }
            else if (!coveredPatterns.Add(arm.Pattern))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3061",
                    DiagnosticSeverity.Error,
                    $"Match arm '{arm.Pattern}' in '{fnName}' is declared more than once.",
                    arm.Location));
            }

            var armType = InferExpressionType(arm.Body, env, context, diagnostics, arm.Location);
            if (armType is UnknownAIType or ErrorAIType)
                continue;

            if (expectedArmType is null)
            {
                expectedArmType = armType;
                continue;
            }

            if (!IsCompatible(armType, expectedArmType) || !IsCompatible(expectedArmType, armType))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3062",
                    DiagnosticSeverity.Error,
                    $"Match arm '{arm.Pattern}' in '{fnName}' has result type '{FormatType(armType)}', which is not compatible with earlier arm result type '{FormatType(expectedArmType)}'.",
                    arm.Location));
            }
        }

        var hasWildcard = coveredPatterns.Contains("_");
        var missing = hasWildcard
            ? []
            : cases.Where(c => !coveredPatterns.Contains(c)).ToList();

        if (missing.Count > 0)
        {
            diagnostics.Add(new Diagnostic(
                "AI3060",
                DiagnosticSeverity.Error,
                $"Non-exhaustive match in '{fnName}' on type '{typeName}': " +
                $"missing cases: {string.Join(", ", missing)}.",
                match.Location));
        }
    }

    private static void CheckReturn(
        ReturnStatement ret,
        string fnName,
        AISharpType declaredReturn,
        Dictionary<string, AISharpType> env,
        TypeContext context,
        List<Diagnostic> diagnostics)
    {
        if (declaredReturn is UnknownAIType)
            return;

        var exprType = InferExpressionType(ret.Expression.Text, env, context, diagnostics, ret.Location);
        if (exprType is UnknownAIType)
            return; // partial typing — can't determine; skip

        if (!IsCompatible(exprType, declaredReturn))
        {
            diagnostics.Add(new Diagnostic(
                "AI3020",
                DiagnosticSeverity.Error,
                $"Return type mismatch in '{fnName}': expression type '{FormatType(exprType)}' " +
                $"is not compatible with declared return type '{FormatType(declaredReturn)}'.",
                ret.Location));
        }
    }

    private static void CheckLet(
        LetStatement let,
        Dictionary<string, AISharpType> env,
        TypeContext context,
        List<Diagnostic> diagnostics)
    {
        if (let.TypeAnnotation is null)
        {
            // No explicit annotation — add inferred type to environment.
            env[let.Name] = InferExpressionType(let.Initializer.Text, env, context, diagnostics, let.Location);
            return;
        }

        var annotType = ParseType(let.TypeAnnotation, context.SymbolTable, context.DeclaredTypes);
        env[let.Name] = annotType;

        var initType = InferExpressionType(let.Initializer.Text, env, context, diagnostics, let.Location);
        if (initType is UnknownAIType)
            return; // partial typing — skip

        if (!IsCompatible(initType, annotType))
        {
            diagnostics.Add(new Diagnostic(
                "AI3021",
                DiagnosticSeverity.Error,
                $"Type annotation mismatch for '{let.Name}': initializer type '{FormatType(initType)}' " +
                $"is not compatible with annotation '{FormatType(annotType)}'.",
                let.Location));
        }
    }

    private static void CheckModelCallOutputSchemas(
        FunctionDeclaration fn,
        SymbolTable symbolTable,
        List<Diagnostic> diagnostics)
    {
        foreach (var mc in fn.ModelCalls)
        {
            if (mc.OutputSchemaReference is null)
                continue;

            var name = mc.OutputSchemaReference.Name;
            if (!symbolTable.ContainsDocument(SymbolKind.Schema, name)
                && !symbolTable.ContainsDocument(SymbolKind.Type, name))
            {
                diagnostics.Add(new Diagnostic(
                    "AI3022",
                    DiagnosticSeverity.Error,
                    $"Model call output schema '{name}' is not declared as a schema or type.",
                    mc.OutputSchemaReference.Location));
            }
        }
    }

    // ── Type parsing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a type annotation string (as it appears in source) to an
    /// <see cref="AISharpType"/>.
    /// </summary>
    public static AISharpType ParseType(string? typeText, SymbolTable symbolTable)
        => ParseType(typeText, symbolTable, declaredTypes: null);

    private static AISharpType ParseType(
        string? typeText,
        SymbolTable symbolTable,
        IReadOnlyDictionary<string, AISharpType>? declaredTypes)
    {
        if (string.IsNullOrWhiteSpace(typeText))
            return AISharpType.Unknown;

        var trimmed = typeText.Trim();

        if (trimmed.StartsWith("fn", StringComparison.Ordinal))
        {
            var fnType = TryParseFunctionType(trimmed, symbolTable, declaredTypes);
            if (fnType is not null)
                return fnType;
        }

        var angleIdx = trimmed.IndexOf('<');
        if (angleIdx > 0 && trimmed.EndsWith('>'))
        {
            var head = trimmed[..angleIdx].Trim();
            var inner = trimmed[(angleIdx + 1)..^1];
            var args = SplitTopLevelTypes(inner)
                .Select(arg => ParseType(arg, symbolTable, declaredTypes))
                .ToList();

            if (string.Equals(head, "Result", StringComparison.Ordinal) && args.Count == 2)
                return new ResultAIType(args[0], args[1]);

            if (string.Equals(head, "Option", StringComparison.Ordinal) && args.Count == 1)
                return new OptionAIType(args[0]);

            return new GenericAIType(head, args);
        }

        // Built-in primitive
        if (s_primitiveNames.Contains(trimmed))
            return new PrimitiveAIType(trimmed);

        // Well-known generic carrier without arguments
        if (s_wellKnownCarriers.Contains(trimmed))
            return new GenericAIType(trimmed, []);

        if (declaredTypes is not null && declaredTypes.TryGetValue(trimmed, out var declaredType))
            return declaredType;

        // Look up in symbol table
        var symbol = symbolTable.LookupDocument(trimmed);
        if (symbol is not null)
        {
            return symbol.Kind switch
            {
                SymbolKind.Resource => new ResourceAIType(trimmed),
                _ => new NamedAIType(trimmed, symbol)
            };
        }

        return new NamedAIType(trimmed, null);
    }

    private static FunctionAIType? TryParseFunctionType(
        string typeText,
        SymbolTable symbolTable,
        IReadOnlyDictionary<string, AISharpType>? declaredTypes)
    {
        var arrow = typeText.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
            return null;

        var parameterPart = typeText[..arrow].Trim();
        var returnPart = typeText[(arrow + 2)..].Trim();
        if (!parameterPart.StartsWith("fn", StringComparison.Ordinal))
            return null;

        parameterPart = parameterPart[2..].Trim();
        if (parameterPart.StartsWith('(') && parameterPart.EndsWith(')'))
            parameterPart = parameterPart[1..^1].Trim();

        var parameters = parameterPart.Length == 0
            ? []
            : SplitTopLevelTypes(parameterPart)
                .Select(parameter => ParseType(parameter, symbolTable, declaredTypes))
                .ToList();

        return new FunctionAIType(parameters, ParseType(returnPart, symbolTable, declaredTypes));
    }

    private static IReadOnlyList<string> SplitTopLevelTypes(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '<' or '(' => 1,
                '>' or ')' => -1,
                _ => 0
            };

            if (text[i] == ',' && depth == 0)
            {
                AddPart(text[start..i]);
                start = i + 1;
            }
        }

        AddPart(text[start..]);
        return result;

        void AddPart(string part)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
    }

    // ── Expression type inference ─────────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex s_outputSchemaPattern =
        new(@"\.output\s*<\s*(?<schema>[A-Za-z_][A-Za-z0-9_#]*(?:\s*<[^>]+>)?)\s*>\s*\(\s*\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex s_simpleCallPattern =
        new(@"^(?<name>[A-Za-z_][A-Za-z0-9_#]*)\s*\((?<args>.*)\)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex s_simpleFieldPattern =
        new(@"^(?<target>[A-Za-z_][A-Za-z0-9_#]*)\.(?<field>[A-Za-z_][A-Za-z0-9_#]*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Infers the type of an expression from its text representation.
    /// Returns <see cref="AISharpType.Unknown"/> when the type cannot be
    /// determined (partial typing: the caller should skip the check).
    /// </summary>
    public static AISharpType InferExpressionType(string? exprText)
        => InferExpressionType(exprText, env: null, context: null, diagnostics: null, location: null);

    private static AISharpType InferExpressionType(
        string? exprText,
        IReadOnlyDictionary<string, AISharpType>? env,
        TypeContext? context,
        List<Diagnostic>? diagnostics,
        SourceLocation? location)
    {
        if (string.IsNullOrWhiteSpace(exprText))
            return AISharpType.Unknown;

        var text = exprText.Trim();

        if (env is not null && env.TryGetValue(text, out var variableType))
            return variableType;

        // String literal
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return new PrimitiveAIType("string");

        // Bool literals
        if (text is "true" or "false")
            return new PrimitiveAIType("bool");

        // Integer literal (possibly negative)
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?\d+$"))
            return new PrimitiveAIType("int");

        // Float literal
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?\d+\.\d+$"))
            return new PrimitiveAIType("float");

        // Ok(…) → compatible with Result<_, _>
        if (text.StartsWith("Ok(", StringComparison.Ordinal) && text.EndsWith(')'))
            return new NamedAIType("Ok", null);

        // Err(…) → compatible with Result<_, _>
        if (text.StartsWith("Err(", StringComparison.Ordinal) && text.EndsWith(')'))
            return new NamedAIType("Err", null);

        // Some(…) → compatible with Option<_>
        if (text.StartsWith("Some(", StringComparison.Ordinal) && text.EndsWith(')'))
            return new NamedAIType("Some", null);

        // None → compatible with Option<_>
        if (text == "None")
            return new NamedAIType("None", null);

        // model.output<Schema>() → typed as Schema
        var outputMatch = s_outputSchemaPattern.Match(text);
        if (outputMatch.Success)
        {
            var schemaName = outputMatch.Groups["schema"].Value.Trim();
            return context is null
                ? new NamedAIType(schemaName, null)
                : ParseType(schemaName, context.SymbolTable, context.DeclaredTypes);
        }

        if (env is not null && context is not null)
        {
            var fieldType = TryInferFieldAccess(text, env, context, diagnostics, location);
            if (fieldType is not null)
                return fieldType;

            var callType = TryInferSimpleCall(text, context);
            if (callType is not null)
                return callType;
        }

        return AISharpType.Unknown;
    }

    private static AISharpType? TryInferSimpleCall(string text, TypeContext context)
    {
        var callMatch = s_simpleCallPattern.Match(text);
        if (!callMatch.Success)
            return null;

        var functionName = callMatch.Groups["name"].Value;
        return context.Functions.TryGetValue(functionName, out var functionType)
            ? functionType.ReturnType
            : null;
    }

    private static AISharpType? TryInferFieldAccess(
        string text,
        IReadOnlyDictionary<string, AISharpType> env,
        TypeContext context,
        List<Diagnostic>? diagnostics,
        SourceLocation? location)
    {
        var fieldMatch = s_simpleFieldPattern.Match(text);
        if (!fieldMatch.Success)
            return null;

        var targetName = fieldMatch.Groups["target"].Value;
        var fieldName = fieldMatch.Groups["field"].Value;
        if (!env.TryGetValue(targetName, out var targetType))
            return AISharpType.Unknown;

        var recordType = ResolveRecordType(targetType, context);
        if (recordType is null)
            return AISharpType.Unknown;

        var field = recordType.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, fieldName, StringComparison.Ordinal));
        if (field is null)
        {
            if (diagnostics is not null && location is not null)
            {
                diagnostics.Add(new Diagnostic(
                    "AI3023",
                    DiagnosticSeverity.Error,
                    $"Record type '{recordType.Name}' does not declare field '{fieldName}'.",
                    location.Value));
            }

            return AISharpType.Error;
        }

        return ParseType(field.Type, context.SymbolTable, context.DeclaredTypes);
    }

    private static RecordAIType? ResolveRecordType(AISharpType type, TypeContext context) =>
        type switch
        {
            RecordAIType record => record,
            NamedAIType named when context.DeclaredTypes.TryGetValue(named.Name, out var declared) =>
                ResolveRecordType(declared, context),
            _ => null
        };

    // ── Compatibility ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when a value of type <paramref name="source"/>
    /// can appear where type <paramref name="target"/> is expected.
    /// </summary>
    public static bool IsCompatible(AISharpType source, AISharpType target)
    {
        // Unknown on either side: skip (partial typing)
        if (source is UnknownAIType || target is UnknownAIType)
            return true;

        // Error type: always compatible (error recovery)
        if (source is ErrorAIType || target is ErrorAIType)
            return true;

        // Identical primitives
        if (source is PrimitiveAIType sp && target is PrimitiveAIType tp)
            return string.Equals(sp.Name, tp.Name, StringComparison.Ordinal);

        // Ok/Err ↔ Result<_, _>
        if (source is NamedAIType { Name: "Ok" or "Err" } && target is GenericAIType { Head: "Result" })
            return true;

        // Some/None ↔ Option<_>
        if (source is NamedAIType { Name: "Some" or "None" } && target is GenericAIType { Head: "Option" })
            return true;

        if (source is NamedAIType { Name: "Ok" or "Err" } && target is ResultAIType)
            return true;

        if (source is NamedAIType { Name: "Some" or "None" } && target is OptionAIType)
            return true;

        // Generic ↔ generic: head-name only
        if (source is GenericAIType sg && target is GenericAIType tg)
            return string.Equals(sg.Head, tg.Head, StringComparison.Ordinal)
                && AreTypeArgumentListsCompatible(sg.Args, tg.Args);

        if (source is ResultAIType sr && target is ResultAIType tr)
            return IsCompatible(sr.OkType, tr.OkType) && IsCompatible(sr.ErrorType, tr.ErrorType);

        if (source is OptionAIType so && target is OptionAIType to)
            return IsCompatible(so.ValueType, to.ValueType);

        if (source is FunctionAIType sf && target is FunctionAIType tf)
            return AreTypeArgumentListsCompatible(sf.Parameters, tf.Parameters)
                && IsCompatible(sf.ReturnType, tf.ReturnType);

        // Named ↔ named
        if (source is NamedAIType sn && target is NamedAIType tn)
            return string.Equals(sn.Name, tn.Name, StringComparison.Ordinal);

        if (source is RecordAIType srec && target is RecordAIType trec)
            return string.Equals(srec.Name, trec.Name, StringComparison.Ordinal);

        if (source is UnionAIType su && target is UnionAIType tu)
            return string.Equals(su.Name, tu.Name, StringComparison.Ordinal)
                || su.Cases.SequenceEqual(tu.Cases, StringComparer.Ordinal);

        if (source is NamedAIType snr && target is RecordAIType trr)
            return string.Equals(snr.Name, trr.Name, StringComparison.Ordinal);

        if (source is RecordAIType srr && target is NamedAIType tnr)
            return string.Equals(srr.Name, tnr.Name, StringComparison.Ordinal);

        if (source is NamedAIType snu && target is UnionAIType tu2)
            return string.Equals(snu.Name, tu2.Name, StringComparison.Ordinal);

        if (source is UnionAIType su2 && target is NamedAIType tnu)
            return string.Equals(su2.Name, tnu.Name, StringComparison.Ordinal);

        // Resource ↔ resource
        if (source is ResourceAIType sourceResource && target is ResourceAIType targetResource)
            return string.Equals(sourceResource.Name, targetResource.Name, StringComparison.Ordinal);

        // Named ↔ generic: e.g. returning a named variant where generic is expected
        if (source is NamedAIType { Name: var srcName } && target is GenericAIType { Head: var tgtHead })
            return string.Equals(srcName, tgtHead, StringComparison.Ordinal);

        // Everything else is incompatible
        return false;
    }

    private static bool AreTypeArgumentListsCompatible(
        IReadOnlyList<AISharpType> source,
        IReadOnlyList<AISharpType> target)
    {
        if (source.Count == 0 || target.Count == 0)
            return true;

        if (source.Count != target.Count)
            return false;

        for (var i = 0; i < source.Count; i++)
        {
            if (!IsCompatible(source[i], target[i]))
                return false;
        }

        return true;
    }

    private static string FormatType(AISharpType type) => type switch
    {
        PrimitiveAIType p => p.Name,
        NamedAIType n => n.Name,
        GenericAIType g when g.Args.Count == 0 => g.Head,
        GenericAIType g => $"{g.Head}<{string.Join(", ", g.Args.Select(FormatType))}>",
        FunctionAIType f => $"fn({string.Join(", ", f.Parameters.Select(FormatType))}) -> {FormatType(f.ReturnType)}",
        ResultAIType r => $"Result<{FormatType(r.OkType)}, {FormatType(r.ErrorType)}>",
        OptionAIType o => $"Option<{FormatType(o.ValueType)}>",
        RecordAIType r => r.Name,
        ResourceAIType r => r.Name,
        UnionAIType u when u.Name is not null => u.Name,
        UnionAIType u => string.Join(" | ", u.Cases),
        UnknownAIType => "unknown",
        ErrorAIType => "error",
        _ => type.ToString() ?? "?"
    };
}
