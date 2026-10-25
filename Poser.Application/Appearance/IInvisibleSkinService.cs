using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>Hiding a human body under its clothing.</summary>
public interface IInvisibleSkinService
{
    /// <summary>Retires state for actors absent from the published scene. Call on the framework thread.</summary>
    void Reconcile(Func<ActorId, bool> isCurrent);
    bool IsHuman(ActorId actor);
    bool IsApplied(ActorId actor);
    Task<string?> Apply(ActorId actor, System.Threading.CancellationToken cancellation);
}

/// <summary>Performs the game-side redraw for clothing-only mode.
/// ClothingOnlyControl checks the session and coordinates concurrent requests.</summary>
public interface IClothingOnlyRuntimePort
{
    /// <summary>Retires state for actors absent from the published scene. Call on the framework thread.</summary>
    void Reconcile(Func<ActorId, bool> isCurrent);
    bool IsHuman(ActorId actor);
    bool IsApplied(ActorId actor);
    Task<string?> Apply(ActorId actor, System.Threading.CancellationToken cancellation);
}
