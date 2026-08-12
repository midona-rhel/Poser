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
    Transform? GetTransformOverride(IActor actor);

    /// <summary>
    /// Sets a transform override for an actor.
    /// </summary>
    void SetTransformOverride(IActor actor, Transform transform);

    /// <summary>
    /// Gets the original (game-controlled) transform of an actor.
    /// </summary>
    Transform GetOriginalTransform(IActor actor);

    /// <summary>
    /// Gets the effective transform (override or original) of an actor.
    /// </summary>
    Transform GetEffectiveTransform(IActor actor);

    /// <summary>
    /// Clears the transform override for an actor.
    /// </summary>
    void ClearTransformOverride(IActor actor);

    /// <summary>
    /// Clears all transform overrides.
    /// </summary>
    void ClearAllOverrides();

    /// <summary>
    /// Checks if an actor has a transform override.
    /// </summary>
    bool HasTransformOverride(IActor actor);
}
