using System;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Provides animation and physics control for actors.
/// </summary>
public interface IAnimationService : IDisposable
{
    /// <summary>
    /// Check if an actor's animation is frozen.
    /// </summary>
    bool IsFrozen(ActorBase actor);

    /// <summary>
    /// Freeze an actor's animation at current frame.
    /// </summary>
    void Freeze(ActorBase actor);

    /// <summary>
    /// Unfreeze an actor's animation.
    /// </summary>
    void Unfreeze(ActorBase actor);

    /// <summary>
    /// Toggle freeze state.
    /// </summary>
    void ToggleFreeze(ActorBase actor);

    /// <summary>
    /// Check if an actor's physics are frozen.
    /// </summary>
    bool IsPhysicsFrozen(ActorBase actor);

    /// <summary>
    /// Freeze an actor's physics (hair, cloth, etc).
    /// </summary>
    void FreezePhysics(ActorBase actor);

    /// <summary>
    /// Unfreeze an actor's physics.
    /// </summary>
    void UnfreezePhysics(ActorBase actor);

    /// <summary>
    /// Toggle physics freeze state.
    /// </summary>
    void TogglePhysicsFreeze(ActorBase actor);

    /// <summary>Use EventBus.Subscribe&lt;FreezeStateChangedEvent&gt; instead.</summary>
    [Obsolete("Use EventBus.Subscribe<FreezeStateChangedEvent> instead.")]
    event Action<ActorBase, bool>? OnFreezeStateChanged;

    /// <summary>Use EventBus.Subscribe&lt;PhysicsFreezeStateChangedEvent&gt; instead.</summary>
    [Obsolete("Use EventBus.Subscribe<PhysicsFreezeStateChangedEvent> instead.")]
    event Action<ActorBase, bool>? OnPhysicsFreezeStateChanged;
}
