using System.Collections.Generic;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

public readonly record struct AnimationPortResult(bool Success, string? Detail = null)
{
    public static AnimationPortResult Ok() => new(true);
    public static AnimationPortResult Fail(string detail) => new(false, detail);
}

/// <summary>Animation operations resolve the current actor before each write.</summary>
public interface IAnimationRuntimePort
{
    /// <summary>True when the actor can be animated.</summary>
    bool IsSupported(ActorId actor);

    /// <summary>Reads one frame of actor animation state.</summary>
    ActorAnimationReading? Read(ActorId actor);

    // ── Base and blend ────────────────────────────────────────────────
    /// <summary>Applies the mode and starts playback.</summary>
    AnimationPortResult Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured);

    /// <summary>Restores the captured base state.</summary>
    AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture);

    /// <summary>Returns the slot used by a stance timeline, if any.</summary>
    AnimationSlot? TimelineSlot(ushort timeline);

    /// <summary>Captures the current base state for restoration.</summary>
    BaseAnimationCapture? CaptureBase(ActorId actor);

    /// <summary>Stops the container's active timeline.</summary>
    AnimationPortResult CancelActiveTimeline(ActorId actor);

    // ── Loops ───────────────────────────────────────────
    /// <summary>Arms looping for one additive slot.</summary>
    AnimationPortResult SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline);
    AnimationPortResult ClearSlotLoop(ActorId actor, AnimationSlot slot);
    /// <summary>Drops every armed loop for the actor.</summary>
    void ClearLoops(ActorId actor);
    /// <summary>Pauses loop enforcement during a multi-step operation.</summary>
    bool LoopsSuspended { get; set; }

    /// <summary>Plays an emote with its intro and loop.</summary>
    AnimationPortResult PlayEmote(ActorId actor, uint emoteId);

    /// <summary>True when main-animation looping is available.</summary>
    bool SupportsForceLoop { get; }

    /// <summary>True when stance-transition functions are available.</summary>
    bool SupportsStance { get; }

    /// <summary>Sets the persistent timeline id; zero clears it.</summary>
    AnimationPortResult SetForceLoop(ActorId actor, ushort timeline);

    // ── Speed ─────────────────────────────────────────────────────────
    AnimationPortResult SetOverallSpeed(ActorId actor, float speed);
    /// <summary>Stops enforcing overall speed.</summary>
    AnimationPortResult ClearOverallSpeed(ActorId actor);

    /// <summary>Rewinds paused controls.</summary>
    AnimationPortResult RewindPausedControls(ActorId actor);
    AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    AnimationPortResult ClearSlotSpeed(ActorId actor, AnimationSlot slot);

    // ── Lips, stance, weapon, position ────────────────────────────────
    AnimationPortResult SetLips(ActorId actor, ushort timeline);
    AnimationPortResult SetStance(ActorId actor, AnimationStance stance, int pose);
    AnimationPortResult SetWeaponDrawn(ActorId actor, bool drawn);
    AnimationPortResult SetPositionLock(ActorId actor, bool locked);

    // ── Scrubbing ─────────────────────────────────────────────────────
    /// <summary>Enumerates controls for a scrub interaction.</summary>
    IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token);

    /// <summary>Finds the live control for a slot.</summary>
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot, out ulong token);

    /// <summary>Writes a control's local time when the token still matches.</summary>
    AnimationPortResult SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token);

    // ── Physics ───────────────────────────────────────────────────────
    /// <summary>Reports the global physics freeze state.</summary>
    bool IsPhysicsFrozen { get; }
    AnimationPortResult SetPhysicsFrozen(bool frozen);
}
