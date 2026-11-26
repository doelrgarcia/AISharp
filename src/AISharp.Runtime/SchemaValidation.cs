using System.Text.Json;

namespace AISharp.Runtime;

// ─── Runtime schema model ─────────────────────────────────────────────────────

/// <summary>Basic AI# field types recognised by the runtime schema validator.</summary>
public enum RuntimeFieldKind
{
    /// <summary>String scalar.</summary>
    String,
    /// <summary>Integer scalar.</summary>
    Int,
    /// <summary>64-bit floating-point scalar.</summary>
    Float,
    /// <summary>Boolean scalar.</summary>
    Bool,
    /// <summary>JSON array; element type is not validated beyond existence.</summary>
    Array,
    /// <summary>Nested JSON object; contents are not recursively validated in this version.</summary>
    Object,
    /// <summary>Unknown or user-defined type; presence is validated but value type is not.</summary>
    Unknown
}

/// <summary>A single field in a <see cref="RuntimeSchema"/>.</summary>
public sealed record RuntimeSchemaField(
    string Name,
    RuntimeFieldKind Kind,
    bool IsOptional = false);

/// <summary>
/// Runtime representation of an AI# schema declaration.
/// Build from compiler <c>SchemaDeclaration</c> using <see cref="RuntimeSchema.FromFields"/>.
/// </summary>
public sealed record RuntimeSchema(
    string Name,
    IReadOnlyList<RuntimeSchemaField> Fields,
    string? SourcePath = null,
    int SourceLine = 0)
{
    /// <summary>Convenience factory: build a schema from name/type string pairs.</summary>
    public static RuntimeSchema FromFields(
        string name,
        IEnumerable<(string FieldName, string TypeName)> fields,
        string? sourcePath = null,
        int sourceLine = 0)
    {
        var runtimeFields = fields
            .Select(f => new RuntimeSchemaField(f.FieldName, ParseKind(f.TypeName)))
            .ToList();
        return new RuntimeSchema(name, runtimeFields, sourcePath, sourceLine);
    }

    internal static RuntimeFieldKind ParseKind(string typeName)
    {
        var normalized = typeName.Trim().ToLowerInvariant();

        if (normalized.EndsWith("[]", StringComparison.Ordinal) ||
            normalized.StartsWith("list<", StringComparison.Ordinal) ||
            normalized.StartsWith("array<", StringComparison.Ordinal) ||
            normalized.StartsWith("ienumerable<", StringComparison.Ordinal))
        {
            return RuntimeFieldKind.Array;
        }

        if (normalized.StartsWith("map<", StringComparison.Ordinal) ||
            normalized.StartsWith("dictionary<", StringComparison.Ordinal))
        {
            return RuntimeFieldKind.Object;
        }

        return normalized switch
        {
            "string" or "str" => RuntimeFieldKind.String,
            "int" or "integer" or "i32" or "i64" => RuntimeFieldKind.Int,
            "float" or "f32" or "f64" or "double" or "number" => RuntimeFieldKind.Float,
            "bool" or "boolean" => RuntimeFieldKind.Bool,
            "array" or "list" or "[]" => RuntimeFieldKind.Array,
            "object" or "map" or "{}" => RuntimeFieldKind.Object,
            _ => RuntimeFieldKind.Unknown
        };
    }
}

// ─── Validation mode ──────────────────────────────────────────────────────────

/// <summary>Controls how extra/unknown JSON fields are handled during schema validation.</summary>
public enum SchemaValidationMode
{
    /// <summary>Extra fields are ignored; only required fields are checked.</summary>
    Default,
    /// <summary>Extra fields cause a validation failure.</summary>
    Strict
}

// ─── Validation errors ────────────────────────────────────────────────────────

/// <summary>Category of a schema validation failure.</summary>
public enum SchemaValidationErrorKind
{
    /// <summary>A required field is absent from the JSON payload.</summary>
    MissingField,
    /// <summary>A field's value type does not match the declared type.</summary>
    WrongType,
    /// <summary>An undeclared field is present and the mode is <see cref="SchemaValidationMode.Strict"/>.</summary>
    ExtraField,
    /// <summary>The payload is not valid JSON.</summary>
    InvalidJson
}

/// <summary>Describes one validation failure found in a JSON payload.</summary>
public sealed record SchemaValidationError(
    SchemaValidationErrorKind Kind,
    string FieldName,
    string Message,
    string SchemaName,
    string? SourcePath = null,
    int SourceLine = 0,
    string? CallSitePath = null,
    int CallSiteLine = 0);

// ─── Validation result ────────────────────────────────────────────────────────

/// <summary>The outcome of validating a JSON payload against a <see cref="RuntimeSchema"/>.</summary>
public sealed record SchemaValidationResult(
    bool IsValid,
    string SchemaName,
    IReadOnlyList<SchemaValidationError> Errors)
{
    /// <summary>Returns a passing result with no errors.</summary>
    public static SchemaValidationResult Pass(string schemaName) =>
        new(true, schemaName, Array.Empty<SchemaValidationError>());
}

// ─── Validator ────────────────────────────────────────────────────────────────

/// <summary>
/// Validates JSON payloads against AI# runtime schemas.
/// Call <see cref="Validate"/> with a parsed or inline schema definition.
/// </summary>
public static class SchemaValidator
{
    /// <summary>
    /// Validates <paramref name="jsonPayload"/> against <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">The schema to validate against.</param>
    /// <param name="jsonPayload">A UTF-8 JSON string.</param>
    /// <param name="mode">Whether to reject extra fields.</param>
    /// <param name="callSitePath">Optional source path for error provenance.</param>
    /// <param name="callSiteLine">Optional call-site line for error provenance.</param>
    public static SchemaValidationResult Validate(
        RuntimeSchema schema,
        string jsonPayload,
        SchemaValidationMode mode = SchemaValidationMode.Default,
        string? callSitePath = null,
        int callSiteLine = 0)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonPayload);
        }
        catch (JsonException ex)
        {
            return new SchemaValidationResult(false, schema.Name,
            [
                new SchemaValidationError(
                    SchemaValidationErrorKind.InvalidJson,
                    "(payload)",
                    $"JSON parse error: {ex.Message}",
                    schema.Name,
                    schema.SourcePath,
                    schema.SourceLine,
                    callSitePath,
                    callSiteLine)
            ]);
        }

        using (doc)
        {
            return ValidateObject(schema, doc.RootElement, mode, callSitePath, callSiteLine);
        }
    }

    private static SchemaValidationResult ValidateObject(
        RuntimeSchema schema,
        JsonElement root,
        SchemaValidationMode mode,
        string? callSitePath,
        int callSiteLine)
    {
        var errors = new List<SchemaValidationError>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SchemaValidationError(
                SchemaValidationErrorKind.InvalidJson,
                "(payload)",
                $"Expected a JSON object at root, got {root.ValueKind}.",
                schema.Name,
                schema.SourcePath,
                schema.SourceLine,
                callSitePath,
                callSiteLine));
            return new SchemaValidationResult(false, schema.Name, errors);
        }

        // Index all present JSON properties.
        var presentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
            presentKeys.Add(prop.Name);

        // Check required fields exist and have correct types.
        foreach (var field in schema.Fields)
        {
            if (!root.TryGetProperty(field.Name, out var value))
            {
                if (!field.IsOptional)
                {
                    errors.Add(new SchemaValidationError(
                        SchemaValidationErrorKind.MissingField,
                        field.Name,
                        $"Required field '{field.Name}' is missing from the JSON payload.",
                        schema.Name,
                        schema.SourcePath,
                        schema.SourceLine,
                        callSitePath,
                        callSiteLine));
                }
                continue;
            }

            if (field.Kind != RuntimeFieldKind.Unknown && !MatchesKind(value, field.Kind))
            {
                errors.Add(new SchemaValidationError(
                    SchemaValidationErrorKind.WrongType,
                    field.Name,
                    $"Field '{field.Name}' expected type {field.Kind} but got {Describe(value)}.",
                    schema.Name,
                    schema.SourcePath,
                    schema.SourceLine,
                    callSitePath,
                    callSiteLine));
            }
        }

        // Strict mode: reject extra fields.
        if (mode == SchemaValidationMode.Strict)
        {
            var declared = new HashSet<string>(
                schema.Fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var key in presentKeys)
            {
                if (!declared.Contains(key))
                {
                    errors.Add(new SchemaValidationError(
                        SchemaValidationErrorKind.ExtraField,
                        key,
                        $"Field '{key}' is not declared in schema '{schema.Name}' (strict mode).",
                        schema.Name,
                        schema.SourcePath,
                        schema.SourceLine,
                        callSitePath,
                        callSiteLine));
                }
            }
        }

        return new SchemaValidationResult(errors.Count == 0, schema.Name, errors);
    }

    private static bool MatchesKind(JsonElement el, RuntimeFieldKind kind) => kind switch
    {
        RuntimeFieldKind.String => el.ValueKind == JsonValueKind.String,
        RuntimeFieldKind.Int => el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out _),
        RuntimeFieldKind.Float => el.ValueKind == JsonValueKind.Number,
        RuntimeFieldKind.Bool => el.ValueKind is JsonValueKind.True or JsonValueKind.False,
        RuntimeFieldKind.Array => el.ValueKind == JsonValueKind.Array,
        RuntimeFieldKind.Object => el.ValueKind == JsonValueKind.Object,
        _ => true
    };

    private static string Describe(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => el.ValueKind.ToString()
    };
}

// ─── Schema registry ──────────────────────────────────────────────────────────

/// <summary>
/// Holds named <see cref="RuntimeSchema"/> instances and performs
/// schema-validated model/tool output checks.
/// </summary>
public sealed class SchemaRegistry
{
    private readonly Dictionary<string, RuntimeSchema> _schemas =
        new(StringComparer.Ordinal);

    /// <summary>Registers a schema. Overwrites any previous registration with the same name.</summary>
    public void Register(RuntimeSchema schema) => _schemas[schema.Name] = schema;

    /// <summary>Returns true when a schema with the given name is registered.</summary>
    public bool Contains(string schemaName) => _schemas.ContainsKey(schemaName);

    /// <summary>
    /// Validates <paramref name="jsonPayload"/> against the schema with the given name.
    /// Throws <see cref="SchemaNotFoundException"/> when the schema is not registered.
    /// </summary>
    public SchemaValidationResult Validate(
        string schemaName,
        string jsonPayload,
        SchemaValidationMode mode = SchemaValidationMode.Default,
        string? callSitePath = null,
        int callSiteLine = 0)
    {
        if (!_schemas.TryGetValue(schemaName, out var schema))
            throw new SchemaNotFoundException(schemaName);
        return SchemaValidator.Validate(schema, jsonPayload, mode, callSitePath, callSiteLine);
    }

    /// <summary>
    /// Validates a model response payload and throws
    /// <see cref="SchemaValidationException"/> on failure.
    /// </summary>
    public void RequireValid(
        string schemaName,
        string jsonPayload,
        SchemaValidationMode mode = SchemaValidationMode.Default,
        string? callSitePath = null,
        int callSiteLine = 0)
    {
        var result = Validate(schemaName, jsonPayload, mode, callSitePath, callSiteLine);
        if (!result.IsValid)
            throw new SchemaValidationException(result);
    }
}

// ─── Schema exceptions ────────────────────────────────────────────────────────

/// <summary>Thrown when a schema lookup fails because the name is not registered.</summary>
public sealed class SchemaNotFoundException : Exception
{
    /// <summary>Initialises a new <see cref="SchemaNotFoundException"/>.</summary>
    public SchemaNotFoundException(string schemaName)
        : base($"AI# schema '{schemaName}' is not registered in the schema registry.")
    {
        SchemaName = schemaName;
    }

    /// <summary>The unresolved schema name.</summary>
    public string SchemaName { get; }
}

/// <summary>Thrown by <see cref="SchemaRegistry.RequireValid"/> when validation fails.</summary>
public sealed class SchemaValidationException : Exception
{
    /// <summary>Initialises a new <see cref="SchemaValidationException"/>.</summary>
    public SchemaValidationException(SchemaValidationResult result)
        : base(FormatMessage(result))
    {
        Result = result;
    }

    /// <summary>The full validation result including all errors.</summary>
    public SchemaValidationResult Result { get; }

    private static string FormatMessage(SchemaValidationResult result)
    {
        var errors = string.Join("; ", result.Errors.Select(e => e.Message));
        return $"AI# schema validation failed for '{result.SchemaName}': {errors}";
    }
}
