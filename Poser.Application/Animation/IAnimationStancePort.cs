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

    Outcome SetStance(ActorId actor, AnimationStance stance, int pose);
    Outcome SetWeaponDrawn(ActorId actor, bool drawn);
    Outcome SetPositionLock(ActorId actor, bool locked);
}
