using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Native stance, weapon-draw and position-lock writes.</summary>
public interface IAnimationStancePort
{
    /// <summary>False when the stance-transition functions (SetEmoteMode /
    /// CancelTimeline) were not found in the running client; surfaces
    /// disable the stance row rather than offer writes that will fail.</summary>
    bool SupportsStance { get; }

    /// <summary>The stance's pose count from the game's pose table (the base pose and
    /// each pose emote); 1 where it can't be read.</summary>
    int PoseCount(AnimationStance stance, bool weaponDrawn);

    Outcome SetStance(ActorId actor, AnimationStance stance, int pose);
    Outcome SetWeaponDrawn(ActorId actor, bool drawn);
    Outcome SetPositionLock(ActorId actor, bool locked);
}
