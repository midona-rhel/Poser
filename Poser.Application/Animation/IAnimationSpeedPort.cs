using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Native animation speed writes. Speed overrides are enforced per frame;
/// clearing one restores its captured value before releasing enforcement.
/// </summary>
public interface IAnimationSpeedPort
{
    Outcome SetOverallSpeed(ActorId actor, float speed);
    /// <summary>Stops enforcing overall speed; the game's own value wins
    /// again from its next recalculation.</summary>
    Outcome ClearOverallSpeed(ActorId actor);

    /// <summary>
    /// Rewinds every paused Havok control to local time zero. Playing
    /// controls are unchanged, and the operation owns no persistent state.
    /// </summary>
    Outcome RewindPausedControls(ActorId actor);
    Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    /// <summary>Releases enforcement after restoring the captured speed.</summary>
    Outcome ClearSlotSpeed(
        ActorId actor, AnimationSlot slot, float restoreSpeed = 1f);
}
