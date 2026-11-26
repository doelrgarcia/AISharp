namespace AISharp.Runtime;

// ─── Effect kinds ─────────────────────────────────────────────────────────────

/// <summary>First-class effect kinds recognised by the AI# runtime.</summary>
[Flags]
public enum EffectKind
{
    None = 0,
    Io = 1 << 0,
    Time = 1 << 1,
    Random = 1 << 2,
    Model = 1 << 3,
    Network = 1 << 4,
    UnsafeFfi = 1 << 5,
    All = Io | Time | Random | Model | Network | UnsafeFfi
}

// ─── Effect scope ─────────────────────────────────────────────────────────────

/// <summary>
/// Represents the currently allowed effects for a call context.
/// AI# runtime code checks this before performing effectful operations.
/// </summary>
public interface IEffectScope
{
    /// <summary>Effects permitted in this scope.</summary>
    EffectKind Allowed { get; }

    /// <summary>Returns true when the given effect kind is allowed.</summary>
    bool IsAllowed(EffectKind effect);
}

/// <summary>
/// Default <see cref="IEffectScope"/> implementation.
/// Use <see cref="EffectRuntimeContext.For"/> to construct scopes.
/// </summary>
public sealed class EffectScope : IEffectScope
{
    internal EffectScope(EffectKind allowed) => Allowed = allowed;

    /// <inheritdoc/>
    public EffectKind Allowed { get; }

    /// <inheritdoc/>
    public bool IsAllowed(EffectKind effect) => (Allowed & effect) == effect;
}

// ─── Effect scope violation ───────────────────────────────────────────────────

/// <summary>Thrown when an AI# function attempts an effect that its scope disallows.</summary>
public sealed class EffectScopeViolationException : Exception
{
    /// <summary>Initialises a new <see cref="EffectScopeViolationException"/>.</summary>
    public EffectScopeViolationException(EffectKind attempted, EffectKind allowed)
        : base($"AI# effect violation: attempted '{attempted}' but only '{allowed}' is allowed in this scope.")
    {
        Attempted = attempted;
        Allowed = allowed;
    }

    /// <summary>The effect that was attempted.</summary>
    public EffectKind Attempted { get; }

    /// <summary>The effects permitted in the violating scope.</summary>
    public EffectKind Allowed { get; }
}

// ─── Effect runtime context ───────────────────────────────────────────────────

/// <summary>
/// Factory and ambient context for AI# effect scopes.
/// Lowered code can call <see cref="Require(IEffectScope, EffectKind)"/> or
/// <see cref="RequireCurrent(EffectKind)"/> before performing effectful operations.
/// </summary>
/// <remarks>
/// <para>
/// The ambient <see cref="Current"/> scope is backed by an
/// <see cref="System.Threading.AsyncLocal{T}"/> so that an enclosing host can
/// limit which effects a guarded operation may perform across async boundaries.
/// The default ambient scope is <see cref="Unrestricted"/>; lowered modules and
/// hosts wrap effectful calls with <see cref="Push(IEffectScope)"/> to apply a
/// narrower scope.
/// </para>
/// </remarks>
public static class EffectRuntimeContext
{
    private static readonly AsyncLocal<IEffectScope?> s_current = new();

    /// <summary>Returns an <see cref="IEffectScope"/> that permits the given effects.</summary>
    public static IEffectScope For(EffectKind allowed) => new EffectScope(allowed);

    /// <summary>An unrestricted scope for top-level / host code that manages its own policy.</summary>
    public static IEffectScope Unrestricted => new EffectScope(EffectKind.All);

    /// <summary>A pure scope that disallows all effects.</summary>
    public static IEffectScope Pure => new EffectScope(EffectKind.None);

    /// <summary>
    /// The currently active ambient effect scope. Defaults to <see cref="Unrestricted"/>
    /// so host-managed code paths are unaffected until they opt in via <see cref="Push"/>.
    /// </summary>
    public static IEffectScope Current => s_current.Value ?? Unrestricted;

    /// <summary>
    /// Pushes <paramref name="scope"/> as the ambient effect scope for the current
    /// execution flow. Dispose the returned token to restore the previous scope.
    /// </summary>
    public static IDisposable Push(IEffectScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = s_current.Value;
        s_current.Value = scope;
        return new PopOnDispose(previous);
    }

    /// <summary>
    /// Verifies that <paramref name="effect"/> is permitted in <paramref name="scope"/>.
    /// Throws <see cref="EffectScopeViolationException"/> if the effect is not allowed.
    /// </summary>
    public static void Require(IEffectScope scope, EffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsAllowed(effect))
            throw new EffectScopeViolationException(effect, scope.Allowed);
    }

    /// <summary>
    /// Verifies that <paramref name="effect"/> is permitted in the ambient
    /// <see cref="Current"/> scope. Throws <see cref="EffectScopeViolationException"/>
    /// if the effect is not allowed. Generated AI# code calls this helper before any
    /// model/network/io/unsafe operation to prevent silent effect escalation.
    /// </summary>
    public static void RequireCurrent(EffectKind effect) => Require(Current, effect);

    private sealed class PopOnDispose : IDisposable
    {
        private readonly IEffectScope? _previous;
        private int _disposed;

        public PopOnDispose(IEffectScope? previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                s_current.Value = _previous;
        }
    }
}

// ─── Effect name parsing ──────────────────────────────────────────────────────

/// <summary>
/// Maps AI# source-level effect names (as they appear in <c>effects [...]</c>
/// signatures) to the runtime <see cref="EffectKind"/> taxonomy.
/// Custom user-declared effects (for example <c>db</c>, <c>json</c>) do not have
/// a built-in <see cref="EffectKind"/>; <see cref="TryParse"/> returns
/// <see langword="false"/> for those names so the runtime can ignore them when
/// emitting kind-based guards while still preserving them in metadata.
/// </summary>
public static class EffectKinds
{
    /// <summary>
    /// Attempts to map the AI# effect name <paramref name="name"/> to a built-in
    /// <see cref="EffectKind"/> flag. Returns <see langword="false"/> for unknown
    /// names so callers can preserve them as metadata-only effects.
    /// </summary>
    public static bool TryParse(string? name, out EffectKind kind)
    {
        switch (name)
        {
            case "io":         kind = EffectKind.Io;        return true;
            case "time":       kind = EffectKind.Time;      return true;
            case "random":     kind = EffectKind.Random;    return true;
            case "model":      kind = EffectKind.Model;     return true;
            case "network":
            case "net":
            case "http":       kind = EffectKind.Network;   return true;
            case "unsafe_ffi":
            case "unsafe":
            case "ffi":        kind = EffectKind.UnsafeFfi; return true;
            default:           kind = EffectKind.None;      return false;
        }
    }
}

// ─── Effect metadata attribute ────────────────────────────────────────────────

/// <summary>
/// Static metadata attached by AI# lowering to every generated function/handler
/// describing the effects declared by the AI# source. Host code can read this
/// attribute via reflection to audit the effect surface of a compiled assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
public sealed class EffectMetadataAttribute : Attribute
{
    /// <summary>Initialises a new <see cref="EffectMetadataAttribute"/>.</summary>
    /// <param name="declared">The union of built-in effect kinds declared on the member.</param>
    /// <param name="effectNames">Comma-separated effect names from the AI# source (e.g. <c>"model,network"</c>).</param>
    public EffectMetadataAttribute(EffectKind declared, string effectNames)
    {
        Declared = declared;
        EffectNames = effectNames ?? string.Empty;
    }

    /// <summary>Built-in effect kinds declared on the member.</summary>
    public EffectKind Declared { get; }

    /// <summary>Comma-separated AI# effect names declared on the member, including custom ones with no built-in kind.</summary>
    public string EffectNames { get; }
}
