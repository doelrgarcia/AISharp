namespace AISharp.Compiler;

/// <summary>A logical type description. This is metadata, not a native storage type.</summary>
public enum ScalarRuntimeTypeKind { Named, Array, Nullable, Constructed, Parameter, NullableDefinition, ValueTupleDefinition, ValueTuple }

public sealed record ScalarRuntimeTypeShape(ScalarRuntimeTypeKind Kind, SourceLocation Location)
{
    public ScalarType? NamedType { get; init; }
    public ScalarRuntimeTypeShape? Element { get; init; }
    public int Rank { get; init; }
    public IReadOnlyList<ScalarRuntimeTypeShape>? Arguments { get; init; }
}

/// <summary>Logical generic declaration facts retained when check-only storage layouts are erased.</summary>
public sealed record ScalarRuntimeGenericParameter(string Name,bool Reference,bool Value,bool Unmanaged,bool Constructor,
    IReadOnlyList<ScalarRuntimeTypeShape> Bounds,IReadOnlyList<int> Dependencies);

public sealed record ScalarRuntimeGenericDefinition(bool IsClass,bool IsInterface,bool IsStatic,int Arity,bool HasConstraints)
{
    public bool HasDefaultConstructor { get; init; }
    public bool HasPhysicalDefaultConstructor { get; init; }
    public bool ValidAsTypeArgument { get; init; } = true;
    public IReadOnlyList<ScalarRuntimeGenericParameter> Parameters { get; init; } = [];
    public ScalarRuntimeTypeShape? BaseType { get; init; }
    public IReadOnlyList<ScalarRuntimeTypeShape> Interfaces { get; init; } = [];
    public IReadOnlyList<ScalarRuntimeTypeShape> InstanceFields { get; init; } = [];
}

/// <summary>A checked generic declaration and its specialization; the declared shape must substitute exactly to the literal shape.</summary>
public sealed record ScalarRuntimeTypeProof(IReadOnlyList<ScalarRuntimeGenericParameter> Parameters,
    IReadOnlyList<ScalarRuntimeTypeShape> Bindings,ScalarRuntimeTypeShape DeclaredShape);
