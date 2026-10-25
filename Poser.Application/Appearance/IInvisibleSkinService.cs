using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>Hiding a human body under its clothing.</summary>
public interface IInvisibleSkinService
{
    bool IsHuman(ActorId actor);
    bool IsApplied(ActorId actor);
    Task<string?> Apply(ActorId actor, System.Threading.CancellationToken cancellation);
}

/// <summary>Performs the game-side redraw for clothing-only mode.
/// ClothingOnlyControl checks the session and coordinates concurrent requests.</summary>
public interface IClothingOnlyRuntimePort
{
    bool IsHuman(ActorId actor);
    bool IsApplied(ActorId actor);
    Task<string?> Apply(ActorId actor, System.Threading.CancellationToken cancellation);
}
