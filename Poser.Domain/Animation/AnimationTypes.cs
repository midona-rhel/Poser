using System;
using System.Collections.Generic;

namespace Poser.Domain.Animation;

/// <summary>
/// The game's animation slots, as Brio enumerates them. Values ARE the
/// native slot indices. 4..6 have no known purpose and are deliberately
/// absent: an absent value cannot be shown or written by mistake.
/// </summary>
public enum AnimationSlot
{
    Base = 0,
    UpperBody = 1,
    Facial = 2,
    Additive = 3,
    Lips = 7,
    Parts1 = 8,
    Parts2 = 9,
    Parts3 = 10,
    Parts4 = 11,
    Overlay = 12,
}

/// <summary>What a catalog entry came from; also the catalog's kind filter.</summary>
public enum AnimationKind
{
    Action,
    Emote,
    Expression,
    RawTimeline,
}

/// <summary>
/// Pose families. Values ARE the game's pose-mode byte (Battle = 1 is
/// deliberately excluded — it is reached through weapon state, not the
/// stance selector).
/// </summary>
public enum AnimationStance
{
    Idle = 0,
    SitChair = 2,
    SitGround = 3,
    Sleeping = 4,
}

public static class AnimationSlots
{
    /// <summary>Every slot Poser exposes, in display order.</summary>
    public static IReadOnlyList<AnimationSlot> All { get; } = new[]
    {
        AnimationSlot.Base, AnimationSlot.UpperBody, AnimationSlot.Facial,
        AnimationSlot.Additive, AnimationSlot.Lips,
        AnimationSlot.Parts1, AnimationSlot.Parts2, AnimationSlot.Parts3,
        AnimationSlot.Parts4, AnimationSlot.Overlay,
    };

    /// <summary>Slots whose Havok control is reliably the friendly
    /// scrub target; everything else scrubs through Advanced.</summary>
    public static IReadOnlyList<AnimationSlot> Scrubbable { get; } = new[]
    {
        AnimationSlot.Base, AnimationSlot.UpperBody,
    };

    public static bool IsKnown(int slot) => slot is 0 or 1 or 2 or 3 or 7 or 8 or 9 or 10 or 11 or 12;

    public static string DisplayName(AnimationSlot slot) => slot switch
    {
        AnimationSlot.Base => "Full body",
        AnimationSlot.UpperBody => "Upper body",
        AnimationSlot.Facial => "Facial",
        AnimationSlot.Additive => "Additive",
        AnimationSlot.Lips => "Lips",
        AnimationSlot.Parts1 => "Parts 1",
        AnimationSlot.Parts2 => "Parts 2",
        AnimationSlot.Parts3 => "Parts 3",
        AnimationSlot.Parts4 => "Parts 4",
        _ => "Overlay",
    };
}

/// <summary>
/// Well-known timeline ids. These are game data, not Poser policy.
/// </summary>
public static class AnimationTimelines
{
    /// <summary>The idle timeline; blending it is how both references
    /// visibly leave an overridden animation.</summary>
    public const ushort Idle = 3;
    /// <summary>The "Straight face" timeline Brio plays to clear a held
    /// expression before returning to idle.</summary>
    public const ushort StraightFace = 604;
    public const ushort DrawWeapon = 1;
    public const ushort SheatheWeapon = 2;
    /// <summary>Battle idle, used when the weapon is drawn.</summary>
    public const ushort BattleIdle = 34;
    /// <summary>Emote id for the battle pose.</summary>
    public const uint BattlePose = 93;
    /// <summary>First and last speech timeline offered as a lip override.</summary>
    public const ushort FirstLips = 0x272;
    public const ushort LastLips = 0x272 + 8;

    /// <summary>Emote ids backing each idle pose index; 0 means "play the
    /// idle timeline instead of an emote".</summary>
    public static IReadOnlyList<uint> IdlePoses { get; } =
        new uint[] { 0, 91, 92, 107, 108, 218, 219 };

    /// <summary>Valid pose count per stance, used for wrapping.</summary>
    public static int PoseCount(AnimationStance stance, bool weaponDrawn) => stance switch
    {
        AnimationStance.Idle => weaponDrawn ? 2 : IdlePoses.Count,
        AnimationStance.SitGround => 4,
        AnimationStance.SitChair => 5,
        AnimationStance.Sleeping => 3,
        _ => 1,
    };

    /// <summary>Wraps a pose index into the stance's valid range in both
    /// directions, so stepping past either end lands on a real pose.</summary>
    public static int WrapPose(int pose, AnimationStance stance, bool weaponDrawn)
    {
        int count = PoseCount(stance, weaponDrawn);
        if (count <= 0)
            return 0;
        return pose < 0 ? count - 1 : pose % count;
    }
}

/// <summary>
/// One catalog row: a playable timeline with the identity needed to find
/// it, display it, and route it to the right slot.
/// </summary>
public sealed record TimelineEntry(
    uint TimelineId,
    string Name,
    AnimationKind Kind,
    AnimationSlot Slot,
    uint Icon = 0,
    uint EmoteId = 0,
    int EmoteIndex = -1)
{
    /// <summary>Emote index 0 is the only one the game can play "from the
    /// start" through its own emote entry point (intro then loop).</summary>
    public bool CanPlayFromStart => Kind is AnimationKind.Emote or AnimationKind.Expression &&
        EmoteIndex == 0 && EmoteId != 0;
}

/// <summary>
/// The exact native state Poser captured before its FIRST base override,
/// and the only thing that can put the actor back. Stored as raw values so
/// the domain never references native enums.
/// </summary>
public readonly record struct BaseAnimationCapture(byte Mode, byte ModeParam, ushort BaseTimeline);

/// <summary>Identity of one Havok animation control, by position. Paired
/// with the skeleton generation it was enumerated under so a scrub can be
/// invalidated instead of writing through a replaced skeleton.</summary>
public readonly record struct ScrubControlId(int Partial, int Control)
{
    public override string ToString() => $"{Partial}.{Control}";
}

public sealed record ScrubControlReading(
    ScrubControlId Id,
    float Time,
    float Duration,
    float PlaybackSpeed);

public sealed record AnimationSlotReading(
    AnimationSlot Slot,
    ushort TimelineId,
    float Speed);

/// <summary>
/// One frame's live native read for an actor. Immutable; valid for the
/// frame it was taken. Poser-owned override state is NOT here — it lives
/// in the session, so the two never drift into two authorities.
/// </summary>
public sealed record ActorAnimationReading(
    ushort BaseTimeline,
    float OverallSpeed,
    ushort LipsOverride,
    bool WeaponDrawn,
    AnimationStance Stance,
    int Pose,
    IReadOnlyList<AnimationSlotReading> Slots,
    IReadOnlyList<ScrubControlReading> Controls,
    ulong SkeletonToken)
{
    public static ActorAnimationReading Empty { get; } = new(
        0, 1f, 0, false, AnimationStance.Idle, 0,
        Array.Empty<AnimationSlotReading>(),
        Array.Empty<ScrubControlReading>(),
        0);

    public ushort TimelineFor(AnimationSlot slot)
    {
        foreach (var entry in Slots)
            if (entry.Slot == slot)
                return entry.TimelineId;
        return 0;
    }

    public float SpeedFor(AnimationSlot slot)
    {
        foreach (var entry in Slots)
            if (entry.Slot == slot)
                return entry.Speed;
        return 1f;
    }
}

/// <summary>
/// Everything Poser authored for one actor. This is the ONLY record of
/// what must be undone; anything absent here was never Poser's to restore.
/// </summary>
public readonly record struct StanceCapture(AnimationStance Stance, int Pose);

public sealed record AnimationOverrides
{
    public ushort? BaseTimeline { get; init; }
    public bool BaseInterrupt { get; init; } = true;
    public bool PlayFromStart { get; init; } = true;
    public float? OverallSpeed { get; init; }
    public IReadOnlyDictionary<AnimationSlot, float> SlotSpeeds { get; init; } =
        new Dictionary<AnimationSlot, float>();
    public ushort? Lips { get; init; }
    public bool PositionLock { get; init; }

    // ── Captures ──────────────────────────────────────────────────────
    // Each is taken ONCE, before the first override of its kind, and is
    // the only thing that can put that aspect back. A capture survives
    // repeated changes so restore always targets the state Poser found,
    // not an intermediate one it created.

    /// <summary>Mode, mode parameter, and base timeline before the first
    /// base override.</summary>
    public BaseAnimationCapture? BaseCapture { get; init; }
    /// <summary>The expression currently HELD on the face: blended onto
    /// the facial layer and pinned there by that layer's speed at 0 --
    /// Brio's mechanism, the only one that exists. Release plays Straight
    /// face then idle and unpins.</summary>
    public ushort? HeldExpression { get; init; }
    /// <summary>Lips timeline before the first lips override. Selecting
    /// None restores THIS, rather than writing 0 — 0 is "no speech
    /// timeline", which is not necessarily what the actor arrived with.</summary>
    public ushort? LipsCapture { get; init; }
    /// <summary>Stance family and pose index before the first stance change.</summary>
    public StanceCapture? StanceCaptureValue { get; init; }
    /// <summary>Weapon state before Poser first drew or sheathed.</summary>
    public bool? WeaponCapture { get; init; }

    public static AnimationOverrides None { get; } = new();

    /// <summary>True when Poser owns anything that must be restored.</summary>
    public bool HasAny =>
        BaseCapture != null || LipsCapture != null || OverallSpeed != null ||
        PositionLock || SlotSpeeds.Count > 0 || HeldExpression != null ||
        StanceCaptureValue != null || WeaponCapture != null;

    public bool IsPaused => OverallSpeed is 0f;
}

/// <summary>
/// Playback options the Animation tab keeps per actor, so switching actors
/// cannot carry one actor's choices onto another. Session-only — never a
/// pose layer, history entry, or file payload.
///
/// Search state is deliberately NOT here: it belongs to the act of
/// picking, lives in the picker, and is cleared each time it opens.
/// </summary>
public sealed record AnimationSelection
{
    public bool PlayAsBase { get; init; } = true;
    public bool Interrupt { get; init; } = true;
    public bool PlayFromStart { get; init; } = true;
    public int DirectTimelineId { get; init; }

    public static AnimationSelection Default { get; } = new();
}
