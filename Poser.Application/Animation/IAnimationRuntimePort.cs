using System.Collections.Generic;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

public readonly record struct AnimationPortResult(bool Success, string? Detail = null)
{
    public static AnimationPortResult Ok() => new(true);
    public static AnimationPortResult Fail(string detail) => new(false, detail);
}

/// <summary>One observed animation command.</summary>
public readonly record struct AnimationProbeCommand(
    string Name,
    AnimationSlot? Slot = null,
    ushort Timeline = 0,
    bool? Enabled = null);

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

    // ── Slot probe ───────────────────────────────────────────────────
    AnimationPortResult StartSlotProbe(ActorId actor);
    AnimationPortResult StopSlotProbe(ActorId actor);
    void BeginSlotProbeCommand(ActorId actor, AnimationProbeCommand command);
    void CompleteSlotProbeCommand(
        ActorId actor, AnimationProbeCommand command, bool success);

    // ── Base and blend ────────────────────────────────────────────────
    /// <summary>
    /// Plays a timeline through the game's own sequencer with the
    /// reference's mode handling: a sheet-Pause timeline holds the actor
    /// (EmoteLoop with parameter 0), and a normal play first leaves a
    /// held or stale-latched mode, which otherwise eats the play. The
    /// timeline row picks its own slot; there is no blend weight
    /// anywhere. When <paramref name="existing"/> is null the pre-play
    /// mode state is captured and returned for restoration.
    /// </summary>
    AnimationPortResult Blend(ActorId actor, ushort timeline,
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
    /// <summary>Arms Poser-driven looping for one slot: whenever the slot
    /// leaves this timeline (the one-shot ended and the game swapped its
    /// own idle in), the timeline is played again through the same proven
    /// sequencer call. The game's forced-timeline field stays unused — it
    /// is unproven for this client.</summary>
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

    /// <summary>
    /// False when the game's persistent forced-timeline field is not mapped
    /// for the running client, in which case <see cref="SetForceLoop"/>
    /// always fails and surfaces must not offer the control. Reported
    /// rather than silently approximated, because every approximation
    /// (latching Base, re-blending idle) changes what the actor is doing.
    /// </summary>
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
    /// Rewinds every PAUSED Havok animation control of the actor to
    /// LocalTime 0, across all partials — the face partial's blink/lip/
    /// expression timeline controls included. Brio's settle rewind
    /// (ActionTimelineCapability.StopSpeedAndResetTimeline,
    /// Brio\Brio\Capabilities\Actor\ActionTimelineCapability.cs:120-165):
    /// run a few ticks AFTER pausing, it snaps every held timeline to its
    /// frame-0 neutral so a pose import diffs against that frame instead
    /// of whatever mid-blink frame the pause happened to catch. Controls
    /// still playing (PlaybackSpeed != 0) are untouched, exactly Brio's
    /// condition. Owns no state; there is nothing to restore.
    /// </summary>
    AnimationPortResult RewindPausedControls(ActorId actor);
    AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed);
    AnimationPortResult ClearSlotSpeed(ActorId actor, AnimationSlot slot);

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
