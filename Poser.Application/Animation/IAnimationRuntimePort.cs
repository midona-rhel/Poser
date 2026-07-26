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
/// The ONE stable-id native boundary for animation. Every member takes an
/// exact-generation <see cref="ActorId"/>; the runtime re-resolves it
/// immediately before touching memory, so a replaced or removed actor
/// fails explicitly instead of writing through a stale pointer. No
/// address, pointer, or retained legacy entity crosses this interface —
/// that is what keeps animation ownership stable across redraws.
///
/// Speed overrides are ENFORCED, not merely written: the implementation
/// registers them so the game's own per-frame recalculation is overridden
/// again each time it runs (Brio's model). Clearing an override therefore
/// hands authority back to the game rather than writing a remembered
/// value.
/// </summary>
public interface IAnimationRuntimePort
{
    /// <summary>True when the actor resolves and can be animated at all
    /// (companions and objects without a character cannot).</summary>
    bool IsSupported(ActorId actor);

    /// <summary>One frame's live native read, or null when unresolvable.</summary>
    ActorAnimationReading? Read(ActorId actor);

    // ── Base and blend ────────────────────────────────────────────────
    /// <summary>
    /// Latches <paramref name="timeline"/> as the actor's base animation.
    /// When <paramref name="capture"/> is supplied it is NOT re-captured —
    /// the first capture is the restore point for the whole session.
    /// Returns the capture taken, if this call took one.
    /// </summary>
    AnimationPortResult ApplyBase(
        ActorId actor, ushort timeline, bool interrupt,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured);

    /// <summary>Puts mode, mode parameter, and base timeline back exactly
    /// as captured, then blends idle so the change is visible.</summary>
    AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture);

    /// <summary>Plays a timeline through the game's own sequencer, which
    /// picks the slot and does the engine's blend. There is no blend
    /// weight or percentage anywhere — the sequencer owns blending.</summary>
    AnimationPortResult Blend(ActorId actor, ushort timeline);

    /// <summary>Plays an emote through the game's emote entry point, which
    /// is the only way to get intro-then-loop playback.</summary>
    AnimationPortResult PlayEmote(ActorId actor, uint emoteId);

    /// <summary>
    /// False when the game's persistent forced-timeline field is not mapped
    /// for the running client, in which case <see cref="SetForceLoop"/>
    /// always fails and surfaces must not offer the control. Reported
    /// rather than silently approximated, because every approximation
    /// (latching Base, re-blending idle) changes what the actor is doing.
    /// </summary>
    bool SupportsForceLoop { get; }

    /// <summary>Writes the forced timeline id the game re-asserts every
    /// frame; 0 clears the loop.</summary>
    AnimationPortResult SetForceLoop(ActorId actor, ushort timeline);

    // ── Speed ─────────────────────────────────────────────────────────
    AnimationPortResult SetOverallSpeed(ActorId actor, float speed);
    /// <summary>Stops enforcing overall speed; the game's own value wins
    /// again from its next recalculation.</summary>
    AnimationPortResult ClearOverallSpeed(ActorId actor);
    AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    AnimationPortResult ClearSlotSpeed(ActorId actor, AnimationSlot slot);

    /// <summary>
    /// Replaces exactly one slot's timeline, leaving every other slot
    /// alone. Refused when the timeline's own slot disagrees with the
    /// target, because writing a facial timeline into the body slot
    /// produces a silently wrong actor rather than an error.
    /// </summary>
    AnimationPortResult SetSlotTimeline(ActorId actor, AnimationSlot slot, ushort timeline);

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
