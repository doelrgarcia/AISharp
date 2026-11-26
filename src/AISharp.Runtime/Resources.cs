namespace AISharp.Runtime;

// ─── Owned resource interface ─────────────────────────────────────────────────

/// <summary>
/// Marker interface for AI# owned resource values.
/// Owned resources are affine: they must be transferred exactly once or disposed deterministically.
/// </summary>
public interface IOwnedResource : IDisposable
{
    /// <summary>Returns true when the resource has been transferred or disposed.</summary>
    bool IsConsumed { get; }
}

/// <summary>
/// Typed wrapper for AI# owned resource values.
/// </summary>
/// <typeparam name="T">The underlying resource type.</typeparam>
public interface IOwnedResource<out T> : IOwnedResource
{
    /// <summary>
    /// Transfers ownership and returns the underlying value.
    /// After this call <see cref="IOwnedResource.IsConsumed"/> returns true.
    /// Calling this a second time throws <see cref="ResourceConsumedException"/>.
    /// </summary>
    T Transfer();
}

// ─── Borrowed resource interface ──────────────────────────────────────────────

/// <summary>
/// Marker interface for AI# borrowed resource values.
/// Borrowed resources cannot escape their lexical lifetime.
/// </summary>
public interface IBorrowedResource
{
    /// <summary>Returns true when the borrow is still valid (within its lexical scope).</summary>
    bool IsValid { get; }
}

/// <summary>Typed borrowed resource reference.</summary>
/// <typeparam name="T">The underlying resource type.</typeparam>
public interface IBorrowedResource<out T> : IBorrowedResource
{
    /// <summary>Reads the underlying value without consuming it.</summary>
    T Value { get; }
}

// ─── Resource consumed exception ──────────────────────────────────────────────

/// <summary>
/// Thrown when AI# runtime code detects use-after-transfer or use-after-disposal
/// of an owned resource.
/// </summary>
public sealed class ResourceConsumedException : Exception
{
    /// <summary>Initialises a new <see cref="ResourceConsumedException"/>.</summary>
    public ResourceConsumedException(string resourceKind)
        : base($"AI# resource '{resourceKind}' has already been transferred or disposed.")
    {
        ResourceKind = resourceKind;
    }

    /// <summary>The kind/name of the consumed resource.</summary>
    public string ResourceKind { get; }
}

// ─── Resource scope ───────────────────────────────────────────────────────────

/// <summary>
/// Represents a lexical lifetime scope for AI# owned resources.
/// Maps to the AI# <c>using resourceName { ... }</c> construct.
/// </summary>
public interface IResourceScope : IDisposable
{
    /// <summary>Registers a resource to be disposed when the scope ends.</summary>
    void Track(IOwnedResource resource);
}

/// <summary>Default <see cref="IResourceScope"/> implementation.</summary>
public sealed class ResourceScope : IResourceScope
{
    private readonly List<IOwnedResource> _resources = new();
    private bool _disposed;

    /// <inheritdoc/>
    public void Track(IOwnedResource resource)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResourceScope));
        _resources.Add(resource);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var resource in _resources)
        {
            if (!resource.IsConsumed)
                resource.Dispose();
        }
    }
}
