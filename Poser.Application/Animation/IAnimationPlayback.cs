using System;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>What the General section of the Animation tab chose for an actor,
/// and the version of it that Apply played. A sitting or mounted actor plays
/// another version of an emote, sometimes on another layer.</summary>
public sealed record GeneralChoice(TimelineEntry Chosen, TimelineEntry? Played);

/// <summary>Playback, transport and physics freeze; native state stays behind the runtime port.</summary>
public interface IAnimationPlayback
{
    bool IsSupported(ActorId actor);
    ActorAnimationReading? Read(ActorId actor);
    AnimationOverrides OverridesFor(ActorId actor);
    bool SupportsStance { get; }
    int PoseCount(AnimationStance stance, bool weaponDrawn);
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
    /// <summary>The General section's choice for the actor, and what Apply played.</summary>
    GeneralChoice? GeneralFor(ActorId actor);
    /// <summary>Remembers the General section's choice without playing it.</summary>
    void ChooseGeneral(ActorId actor, TimelineEntry entry);
    /// <summary>Plays an ended layer again, held, for a scrub.</summary>
    Outcome ReplayEndedForScrub(ActorId actor, AnimationSlot slot);
    /// <summary>Seeks a layer by its own timeline clock (seconds from its begin).</summary>
    Outcome SeekLayer(ActorId actor, AnimationSlot slot, float seconds);
    /// <summary>What Poser holds in the actor's animation, for undo.</summary>
    AnimationSnapshot Capture(ActorId actor);
    /// <summary>Puts a captured animation back without recording another undo step.</summary>
    Outcome Restore(ActorId actor, AnimationSnapshot target);
    /// <summary>Puts back everything Poser changed in the actor's animation.</summary>
    Outcome ResetActor(ActorId actor);
    /// <summary>Moves every paused animation back to its first frame. Not an undo step.</summary>
    Outcome RewindPausedControls(ActorId actor);
    /// <summary>Records no animation undo steps until the returned scope is disposed.</summary>
    IDisposable Untracked();
    /// <summary>Runs several commands as one undo step.</summary>
    void Grouped(Action action);
}
