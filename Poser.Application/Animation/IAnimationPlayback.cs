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
    /// <summary>The layer's held blend weight, or 1 when Poser holds none.</summary>
    float WeightFor(ActorId actor, AnimationSlot slot);
    /// <summary>Holds a layer's blend weight in [0, 1]; 1 releases it.</summary>
    Outcome SetSlotWeight(ActorId actor, AnimationSlot slot, float weight);
    Outcome Pause(ActorId actor);
    Outcome Resume(ActorId actor);
    Outcome PauseSlot(ActorId actor, AnimationSlot slot);
    /// <summary>Plays one held layer on from where it holds; never applies a pending selection.</summary>
    Outcome ResumeSlot(ActorId actor, AnimationSlot slot);
    Outcome SetStance(ActorId actor, AnimationStance stance, int pose);
    Outcome SetWeaponDrawn(ActorId actor, bool drawn);
    Outcome SetPositionLock(ActorId actor, bool locked);
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot);
    bool IsAdvanced(ActorId actor);
    /// <summary>Starts a drag; with <paramref name="slot"/> only that layer is held.</summary>
    Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null);
    Outcome UpdateScrub(ActorId actor, float time, Guid owner);
    void EndScrub(Guid owner);
    /// <summary>The length in seconds of a layer the game ended (its row stays),
    /// or null while it plays.</summary>
    float? EndedLength(ActorId actor, AnimationSlot slot);
    /// <summary>The General Apply's emote for the actor's posture (null, with the
    /// reason, where the game has none).</summary>
    TimelineEntry? PostureEntry(ActorId actor, TimelineEntry entry, out string? refusal);
    /// <summary>Plays an ended layer again, held, for a scrub.</summary>
    Outcome ReplayEndedForScrub(ActorId actor, AnimationSlot slot);
    /// <summary>Seeks a layer by its own timeline clock (seconds from its begin).</summary>
    Outcome SeekLayer(ActorId actor, AnimationSlot slot, float seconds);
}
