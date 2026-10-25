using System;
using Poser.Domain.Transforms;
using Poser.Game.Entities;

namespace Poser.Game.Services;

/// <summary>Reads and owns actor transform overrides.</summary>
public interface IPosingService : IDisposable
{
    Transform? GetTransformOverride(IActor actor);

    /// <summary>
    /// Applies the actor's transform, then keeps it in place until the override
    /// is cleared. Returns false if the change could not be applied.
    /// </summary>
    bool SetTransformOverride(IActor actor, Transform transform);

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

    bool HasTransformOverride(IActor actor);
}
