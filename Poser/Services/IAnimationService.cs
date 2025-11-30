using System;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Provides animation control for actors (freeze/unfreeze).
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

    event Action<ActorBase, bool>? OnFreezeStateChanged;
}
