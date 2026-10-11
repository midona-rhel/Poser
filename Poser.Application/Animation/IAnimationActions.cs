using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Animation actions that share the application's value history.</summary>
public interface IAnimationActions
{
    Outcome SetAdvanced(ActorId actor, bool enabled);
    Outcome Play(ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume = true);
    Outcome ResetSlot(ActorId actor, AnimationSlot slot);
    Outcome SetLoop(ActorId actor, AnimationSlot slot, bool on);
    Outcome ResetGeneral(ActorId actor);
}
