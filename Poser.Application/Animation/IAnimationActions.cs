using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Animation actions that share the application's value history.</summary>
public interface IAnimationActions
{
    AnimationResult Play(ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume = true);
    AnimationResult ResetSlot(ActorId actor, AnimationSlot slot);
    AnimationResult SetLoop(ActorId actor, AnimationSlot slot, bool on);
    AnimationResult ResetGeneral(ActorId actor);
    AnimationResult ResetLayers(ActorId actor);
}
