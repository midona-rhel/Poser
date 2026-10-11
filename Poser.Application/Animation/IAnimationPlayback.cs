using Poser.Domain;
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
    Outcome ChooseSlot(ActorId actor, AnimationSlot slot, ushort timeline);
    Outcome SetSpeed(ActorId actor, float speed);
    Outcome ClearSpeed(ActorId actor);
    bool IsPhysicsFrozen { get; }
    Outcome SetScenePhysicsFrozen(bool frozen);
    Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    Outcome Pause(ActorId actor);
    Outcome Resume(ActorId actor);
    Outcome PauseSlot(ActorId actor, AnimationSlot slot);
    Outcome SetStance(ActorId actor, AnimationStance stance, int pose);
    Outcome SetWeaponDrawn(ActorId actor, bool drawn);
    Outcome SetPositionLock(ActorId actor, bool locked);
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot);
    bool IsAdvanced(ActorId actor);
    Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner);
    Outcome UpdateScrub(ActorId actor, float time, Guid owner);
    void EndScrub(Guid owner);
}
