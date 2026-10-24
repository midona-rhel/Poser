using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>Hiding a human body under its clothing.</summary>
public interface IInvisibleSkinService
{
    bool IsHuman(ActorId actor);
    bool IsApplied(ActorId actor);
    Task<string?> Apply(ActorId actor, System.Threading.CancellationToken cancellation);
}

/// <summary>Framework-aware native mechanism; command admission belongs to ClothingOnlyControl.</summary>
public interface IClothingOnlyRuntimePort
{
    bool IsHuman(ActorId actor);
    bool IsApplied(ActorId actor);
    Task<string?> Apply(ActorId actor, System.Threading.CancellationToken cancellation);
}
