using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Playback, transport and physics freeze; native state stays behind the runtime port.</summary>
public interface IAnimationPlayback
{
    bool IsSupported(ActorId actor);
    ActorAnimationReading? Read(ActorId actor);
    AnimationOverrides OverridesFor(ActorId actor);
    bool SupportsStance { get; }
    bool OwnsSlot(ActorId actor, AnimationSlot slot);
    ushort? SelectedFor(ActorId actor, AnimationSlot slot);
    bool LoopWantedFor(ActorId actor, AnimationSlot slot);
    ushort? HeldExpressionFor(ActorId actor);
    bool IsPaused(ActorId actor);
    bool AnyPlaying(ActorId actor);
    AnimationResult ChooseSlot(ActorId actor, AnimationSlot slot, ushort timeline);
    AnimationResult SetSpeed(ActorId actor, float speed);
    AnimationResult ClearSpeed(ActorId actor);
    bool IsPhysicsFrozen { get; }
    AnimationResult SetScenePhysicsFrozen(bool frozen);
    AnimationResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    AnimationResult Pause(ActorId actor);
    AnimationResult Resume(ActorId actor);
    AnimationResult PauseSlot(ActorId actor, AnimationSlot slot);
    AnimationResult SetStance(ActorId actor, AnimationStance stance, int pose);
    AnimationResult SetWeaponDrawn(ActorId actor, bool drawn);
    AnimationResult SetPositionLock(ActorId actor, bool locked);
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot);
    AnimationResult BeginScrub(ActorId actor, ScrubControlId control);
    AnimationResult UpdateScrub(ActorId actor, float time);
    void EndScrub();
}
