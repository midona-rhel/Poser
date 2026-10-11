using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Native animation boundary keyed by exact actor generation. The runtime
/// resolves the actor immediately before each memory operation. This port
/// owns reads, timeline playback, loops and lips; speed, stance and scrub
/// writes sit on their own ports.
/// </summary>
public interface IAnimationTimelinePort
{
    /// <summary>True when the actor resolves and can be animated at all
    /// (companions and objects without a character cannot).</summary>
    bool IsSupported(ActorId actor);

    /// <summary>One frame's live native read, or null when unresolvable.</summary>
    ActorAnimationReading? Read(ActorId actor);

    // ── Base and blend ────────────────────────────────────────────────
    /// <summary>Plays a timeline and captures the first base state.</summary>
    Outcome Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured);

    /// <summary>Clears the forced base timeline, then plays a base timeline.</summary>
    Outcome PlayBase(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured);

    /// <summary>Puts mode, mode parameter, and the base-override field
    /// back exactly as captured, then replays the captured base-slot
    /// timeline (idle only as fallback).</summary>
    Outcome RestoreBase(ActorId actor, BaseAnimationCapture capture);

    /// <summary>The slot the sheet's Stance column routes a timeline onto,
    /// or null when unmapped — how the session knows which slot's incoming
    /// timeline a play is about to overwrite.</summary>
    AnimationSlot? TimelineSlot(ushort timeline);

    /// <summary>The base restore point as it stands right now, for plays
    /// that go through the emote entry point rather than Blend.</summary>
    BaseAnimationCapture? CaptureBase(ActorId actor);

    /// <summary>Cancels the container's running timeline. The available
    /// native operation is container-wide rather than slot-specific.</summary>
    Outcome CancelActiveTimeline(ActorId actor);

    /// <summary>Clears one layered slot: its sequencer id entries and the
    /// active timeline, so a following base write cannot re-schedule it.</summary>
    Outcome ClearSlotTimeline(ActorId actor, AnimationSlot slot);

    // ── Loops ───────────────────────────────────────────
    /// <summary>Arms exact-slot replay for verified Base or Upper ownership.</summary>
    Outcome SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline);
    Outcome ClearSlotLoop(ActorId actor, AnimationSlot slot);
    /// <summary>Drops every armed loop for the actor. No native writes.</summary>
    void ClearLoops(ActorId actor);
    /// <summary>Pauses loop enforcement while a multi-phase operation
    /// (facial bake) needs the actor to hold still.</summary>
    bool LoopsSuspended { get; set; }

    /// <summary>Plays an emote through the game's emote entry point, which
    /// is the only way to get intro-then-loop playback.</summary>
    Outcome PlayEmote(ActorId actor, uint emoteId);

    /// <summary>Whether full-body repeat is available.</summary>
    bool SupportsForceLoop { get; }

    /// <summary>Owns the forced timeline id until a zero write releases it.
    /// The runtime reasserts an armed id after native animation updates.</summary>
    Outcome SetForceLoop(ActorId actor, ushort timeline);

    // ── Lips ──────────────────────────────────────────────────────────
    Outcome SetLips(ActorId actor, ushort timeline);
}
