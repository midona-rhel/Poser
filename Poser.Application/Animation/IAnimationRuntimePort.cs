using System.Collections.Generic;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

public readonly record struct AnimationPortResult(bool Success, string? Detail = null)
{
    public static AnimationPortResult Ok() => new(true);
    public static AnimationPortResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// Native animation boundary keyed by exact actor generation. The runtime
/// resolves the actor immediately before each memory operation. Speed
/// overrides are enforced per frame; clearing one restores its captured
/// value before releasing enforcement.
/// </summary>
public interface IAnimationRuntimePort
{
    /// <summary>True when the actor resolves and can be animated at all
    /// (companions and objects without a character cannot).</summary>
    bool IsSupported(ActorId actor);

    /// <summary>One frame's live native read, or null when unresolvable.</summary>
    ActorAnimationReading? Read(ActorId actor);

    // ── Base and blend ────────────────────────────────────────────────
    /// <summary>Plays a timeline and captures the first base state.</summary>
    AnimationPortResult Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured);

    /// <summary>Clears the forced base timeline, then plays a base timeline.</summary>
    AnimationPortResult PlayBase(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured);

    /// <summary>Puts mode, mode parameter, and the base-override field
    /// back exactly as captured, then replays the captured base-slot
    /// timeline (idle only as fallback).</summary>
    AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture);

    /// <summary>The slot the sheet's Stance column routes a timeline onto,
    /// or null when unmapped — how the session knows which slot's incoming
    /// timeline a play is about to overwrite.</summary>
    AnimationSlot? TimelineSlot(ushort timeline);

    /// <summary>The base restore point as it stands right now, for plays
    /// that go through the emote entry point rather than Blend.</summary>
    BaseAnimationCapture? CaptureBase(ActorId actor);

    /// <summary>The game's own cancellation of the container's running
    /// timeline (the stance transition's function; container-wide, since
    /// no per-slot stop is proven in either reference).</summary>
    AnimationPortResult CancelActiveTimeline(ActorId actor);

    // ── Loops ───────────────────────────────────────────
    /// <summary>Arms a legacy replay loop.</summary>
    AnimationPortResult SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline);
    AnimationPortResult ClearSlotLoop(ActorId actor, AnimationSlot slot);
    /// <summary>Drops every armed loop for the actor. No native writes.</summary>
    void ClearLoops(ActorId actor);
    /// <summary>Pauses loop enforcement while a multi-phase operation
    /// (facial bake) needs the actor to hold still.</summary>
    bool LoopsSuspended { get; set; }

    /// <summary>Plays an emote through the game's emote entry point, which
    /// is the only way to get intro-then-loop playback.</summary>
    AnimationPortResult PlayEmote(ActorId actor, uint emoteId);

    /// <summary>Whether full-body repeat is available.</summary>
    bool SupportsForceLoop { get; }

    /// <summary>False when the stance-transition functions (SetEmoteMode /
    /// CancelTimeline) were not found in the running client; surfaces
    /// disable the stance row rather than offer writes that will fail.</summary>
    bool SupportsStance { get; }

    /// <summary>Writes the forced timeline id the game re-asserts every
    /// frame; 0 clears the loop.</summary>
    AnimationPortResult SetForceLoop(ActorId actor, ushort timeline);

    // ── Speed ─────────────────────────────────────────────────────────
    AnimationPortResult SetOverallSpeed(ActorId actor, float speed);
    /// <summary>Stops enforcing overall speed; the game's own value wins
    /// again from its next recalculation.</summary>
    AnimationPortResult ClearOverallSpeed(ActorId actor);

    /// <summary>
    /// Rewinds every paused Havok control to local time zero. Playing
    /// controls are unchanged, and the operation owns no persistent state.
    /// </summary>
    AnimationPortResult RewindPausedControls(ActorId actor);
    AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    /// <summary>Releases enforcement after restoring the captured speed.</summary>
    AnimationPortResult ClearSlotSpeed(
        ActorId actor, AnimationSlot slot, float restoreSpeed = 1f);

    // ── Lips, stance, weapon, position ────────────────────────────────
    AnimationPortResult SetLips(ActorId actor, ushort timeline);
    AnimationPortResult SetStance(ActorId actor, AnimationStance stance, int pose);
    AnimationPortResult SetWeaponDrawn(ActorId actor, bool drawn);
    AnimationPortResult SetPositionLock(ActorId actor, bool locked);

    // ── Scrubbing ─────────────────────────────────────────────────────
    /// <summary>Every currently valid Havok control, freshly enumerated.
    /// The returned <c>SkeletonToken</c> on the reading identifies the
    /// enumeration; writing with a stale token is refused.</summary>
    IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token);

    /// <summary>
    /// The control driving a specific slot, by the reference lookup
    /// (control index == slot index, searched across partials) rather than
    /// by position in the flattened list. Null when the slot is empty or
    /// has no such control. Only Base and UpperBody are supported; the
    /// correspondence does not hold for the other slots.
    /// </summary>
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot, out ulong token);

    /// <summary>Writes a control's local time. Fails when the actor,
    /// skeleton, or control no longer matches <paramref name="token"/>,
    /// so a scrub can never land on a replaced skeleton.</summary>
    AnimationPortResult SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token);

    // ── Physics ───────────────────────────────────────────────────────
    /// <summary>Physics freeze is a global code patch, not per-actor; the
    /// session still records who asked so the last release restores it.</summary>
    bool IsPhysicsFrozen { get; }
    AnimationPortResult SetPhysicsFrozen(bool frozen);
}
