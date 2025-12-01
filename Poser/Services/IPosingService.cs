using System;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for applying transform overrides to actors.
/// </summary>
public interface IPosingService : IDisposable
{
    /// <summary>
    /// Gets the current transform override for an actor, if any.
    /// </summary>
    Transform? GetTransformOverride(ActorBase actor);

    /// <summary>
    /// Sets a transform override for an actor.
    /// </summary>
    void SetTransformOverride(ActorBase actor, Transform transform);

    /// <summary>
    /// Sets the position of an actor, preserving existing rotation and scale overrides.
    /// </summary>
    void SetPosition(ActorBase actor, System.Numerics.Vector3 position);

    /// <summary>
    /// Sets the rotation of an actor, preserving existing position and scale overrides.
    /// </summary>
    void SetRotation(ActorBase actor, System.Numerics.Quaternion rotation);

    /// <summary>
    /// Sets the scale of an actor, preserving existing position and rotation overrides.
    /// </summary>
    void SetScale(ActorBase actor, System.Numerics.Vector3 scale);

    /// <summary>
    /// Gets the original (game-controlled) transform of an actor.
    /// </summary>
    Transform GetOriginalTransform(ActorBase actor);

    /// <summary>
    /// Gets the effective transform (override or original) of an actor.
    /// </summary>
    Transform GetEffectiveTransform(ActorBase actor);

    /// <summary>
    /// Clears the transform override for an actor.
    /// </summary>
    void ClearTransformOverride(ActorBase actor);

    /// <summary>
    /// Clears all transform overrides.
    /// </summary>
    void ClearAllOverrides();

    /// <summary>
    /// Checks if an actor has a transform override.
    /// </summary>
    bool HasTransformOverride(ActorBase actor);
}
