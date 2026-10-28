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
    /// <summary>Forgets speed overrides for a removed actor without writing game memory.</summary>
    void Forget(ActorId actor);

    /// <summary>Stops enforcing overall speed so the game can update it again.</summary>
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

    /// <summary>Holds a layer's blend weight in [0, 1], re-applied to every
    /// animation the layer starts.</summary>
    Outcome SetSlotWeight(ActorId actor, AnimationSlot slot, float weight) =>
        Outcome.Fail("Layer weight is unavailable.");

    /// <summary>Releases a held layer weight (back to the game's 1).</summary>
    Outcome ClearSlotWeight(ActorId actor, AnimationSlot slot) => Outcome.Ok();
}
