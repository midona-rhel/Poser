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
    bool IsFrozen(IActor actor);

    /// <summary>
    /// Freeze an actor's animation at current frame.
    /// </summary>
    void Freeze(IActor actor);

    /// <summary>
    /// Unfreeze an actor's animation.
    /// </summary>
    void Unfreeze(IActor actor);

    /// <summary>
    /// Toggle freeze state.
    /// </summary>
    void ToggleFreeze(IActor actor);

    /// <summary>
    /// Check if an actor's physics are frozen.
    /// </summary>
    bool IsPhysicsFrozen(IActor actor);

    /// <summary>
    /// Freeze an actor's physics (hair, cloth, etc).
    /// </summary>
    void FreezePhysics(IActor actor);

    /// <summary>
    /// Unfreeze an actor's physics.
    /// </summary>
    void UnfreezePhysics(IActor actor);

    /// <summary>
    /// Toggle physics freeze state.
    /// </summary>
    void TogglePhysicsFreeze(IActor actor);

    #region Speed Control

    /// <summary>
    /// Gets the current animation speed for an actor (1.0 = normal).
    /// </summary>
    float GetSpeed(IActor actor);

    /// <summary>
    /// Sets the animation speed for an actor.
    /// </summary>
    void SetSpeed(IActor actor, float speed);

    /// <summary>
    /// Resets animation speed to normal (1.0).
    /// </summary>
    void ResetSpeed(IActor actor);

    #endregion

    #region Animation Scrubbing

    /// <summary>
    /// Gets the total duration of the current animation, or null if not available.
    /// </summary>
    float? GetAnimationDuration(IActor actor);

    /// <summary>
    /// Gets the current time position in the animation, or null if not available.
    /// </summary>
    float? GetAnimationTime(IActor actor);

    /// <summary>
    /// Sets the current time position in the animation.
    /// </summary>
    void SetAnimationTime(IActor actor, float time);

    #endregion

    #region Base/Blend Animation

    /// <summary>
    /// Applies a base animation override.
    /// </summary>
    void ApplyBaseAnimation(IActor actor, ushort timelineId, bool interrupt);

    /// <summary>
    /// Stops the base animation override and returns to normal.
    /// </summary>
    void StopBaseAnimation(IActor actor);

    /// <summary>
    /// Checks if the actor has a base animation override active.
    /// </summary>
    bool HasBaseOverride(IActor actor);

    /// <summary>
    /// Plays a blend animation on top of the current animation.
    /// </summary>
    void PlayBlendAnimation(IActor actor, ushort timelineId);

    #endregion

}
