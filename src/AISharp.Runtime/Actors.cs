namespace AISharp.Runtime;

// ─── Actor identity ───────────────────────────────────────────────────────────

/// <summary>Stable identifier for an AI# actor instance.</summary>
public sealed record ActorIdentity(string ActorType, string InstanceId)
{
    /// <inheritdoc/>
    public override string ToString() => $"{ActorType}/{InstanceId}";
}

// ─── Actor message context ────────────────────────────────────────────────────

/// <summary>
/// Context supplied to an actor handler invocation.
/// Provides identity, effect scope, policy context, and cancellation.
/// </summary>
public interface IActorMessageContext
{
    /// <summary>Identity of the actor receiving the message.</summary>
    ActorIdentity Identity { get; }

    /// <summary>Effects permitted for this handler invocation.</summary>
    IEffectScope EffectScope { get; }

    /// <summary>Active policy context.</summary>
    PolicyRuntimeContext Policy { get; }

    /// <summary>Cancellation token for this handler invocation.</summary>
    CancellationToken Cancellation { get; }
}

// ─── Actor interface ──────────────────────────────────────────────────────────

/// <summary>
/// Runtime boundary interface for AI# actors.
/// AI# actors are isolated stateful services; the runtime ensures handlers
/// run serially with access only to declared state members.
/// </summary>
public interface IAISharpActor
{
    /// <summary>Stable identity of this actor instance.</summary>
    ActorIdentity Identity { get; }

    /// <summary>
    /// Activates the actor, initialising state to declared zero-values.
    /// Called once before any handler is dispatched.
    /// </summary>
    Task ActivateAsync(IActorMessageContext context);

    /// <summary>
    /// Deactivates the actor and releases any held resources.
    /// Called when the actor is being stopped or garbage collected.
    /// </summary>
    Task DeactivateAsync(IActorMessageContext context);
}

// ─── Actor runtime host interface ────────────────────────────────────────────

/// <summary>
/// Minimal host interface for the AI# actor runtime profile.
/// A host is responsible for routing messages to actor instances,
/// managing actor lifecycle, and serialising handler invocations.
/// </summary>
public interface IActorRuntimeHost
{
    /// <summary>
    /// Activates or retrieves an existing actor of the given type and instance ID.
    /// </summary>
    Task<IAISharpActor> GetOrActivateAsync(
        string actorType,
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates and removes an actor instance.
    /// </summary>
    Task DeactivateAsync(ActorIdentity identity, CancellationToken cancellationToken = default);
}

// ─── Default actor message context ───────────────────────────────────────────

/// <summary>Default implementation of <see cref="IActorMessageContext"/>.</summary>
public sealed class ActorMessageContext : IActorMessageContext
{
    /// <summary>Initialises an <see cref="ActorMessageContext"/>.</summary>
    public ActorMessageContext(
        ActorIdentity identity,
        IEffectScope effectScope,
        PolicyRuntimeContext policy,
        CancellationToken cancellation = default)
    {
        Identity = identity;
        EffectScope = effectScope;
        Policy = policy;
        Cancellation = cancellation;
    }

    /// <inheritdoc/>
    public ActorIdentity Identity { get; }

    /// <inheritdoc/>
    public IEffectScope EffectScope { get; }

    /// <inheritdoc/>
    public PolicyRuntimeContext Policy { get; }

    /// <inheritdoc/>
    public CancellationToken Cancellation { get; }
}
