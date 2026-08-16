using System;
using System.Collections.Generic;

namespace Poser.Domain.Animation;

/// <summary>Animation slots exposed by the application.</summary>
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

/// <summary>Pose families used by the stance picker.</summary>
public enum AnimationStance
{
    Idle = 0,
    WeaponDrawn = 1,
    SitChair = 2,
    SitGround = 3,
    Sleeping = 4,
    Umbrella = 5,
    Accessory = 6,
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

    /// <summary>Slots with a direct scrub control.</summary>
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

/// <summary>Well-known timeline ids.</summary>
public static class AnimationTimelines
{
    /// <summary>The standard idle timeline.</summary>
    public const ushort Idle = 3;
    /// <summary>The neutral facial timeline.</summary>
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

    /// <summary>Emote ids for idle pose indices. Zero uses the idle timeline.</summary>
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

/// <summary>A playable catalog timeline and its display metadata.</summary>
public sealed record TimelineEntry(
    uint TimelineId,
    string Name,
    AnimationKind Kind,
    AnimationSlot Slot,
    uint Icon = 0,
    uint EmoteId = 0,
    int EmoteIndex = -1,
    bool? DrawsWeapon = null)
{
    /// <summary>True when the entry supports intro playback.</summary>
    public bool CanPlayFromStart => Kind is AnimationKind.Emote or AnimationKind.Expression &&
        EmoteIndex == 0 && EmoteId != 0;
}

/// <summary>State captured before the base animation changes.</summary>
public readonly record struct BaseAnimationCapture(
    byte Mode,
    uint ModeParam,
    ushort BaseTimeline,
    ushort BaseSlotTimeline = 0,
    ushort ForcedTimeline = 0);

/// <summary>Identifies an animation control by position.</summary>
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

/// <summary>Immutable animation state read for one frame.</summary>
public sealed record ActorAnimationReading(
    ushort BaseTimeline,
    float OverallSpeed,
    ushort LipsOverride,
    bool WeaponDrawn,
    AnimationStance Stance,
    int Pose,
    IReadOnlyList<AnimationSlotReading> Slots)
{
    public static ActorAnimationReading Empty { get; } = new(
        0, 1f, 0, false, AnimationStance.Idle, 0,
        Array.Empty<AnimationSlotReading>());

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

/// <summary>Animation state owned by Poser for one actor.</summary>
public readonly record struct StanceCapture(AnimationStance Stance, int Pose);

public sealed record AnimationOverrides
{
    public ushort? BaseTimeline { get; init; }
    public float? OverallSpeed { get; init; }
    /// <summary>Explicitly armed layer loops.</summary>
    public IReadOnlyDictionary<AnimationSlot, ushort> LoopedSlots { get; init; } =
        new Dictionary<AnimationSlot, ushort>();
    /// <summary>Captured incoming timelines for non-base slots.</summary>
    public IReadOnlyDictionary<AnimationSlot, ushort> SlotCaptures { get; init; } =
        new Dictionary<AnimationSlot, ushort>();
    public IReadOnlyDictionary<AnimationSlot, float> SlotSpeeds { get; init; } =
        new Dictionary<AnimationSlot, float>();
    public ushort? Lips { get; init; }
    public bool PositionLock { get; init; }

    // ── Captures ──────────────────────────────────────────────────────
    // Captures are taken before the first override.

    /// <summary>Base state captured before the first Poser play.</summary>
    public BaseAnimationCapture? BaseCapture { get; init; }
    /// <summary>The expression currently held on the facial layer.</summary>
    public ushort? HeldExpression { get; init; }
    /// <summary>Lips timeline captured before the first override.</summary>
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
        LoopedSlots.Count > 0 || SlotCaptures.Count > 0 ||
        StanceCaptureValue != null || WeaponCapture != null;

    public bool IsPaused => OverallSpeed is 0f;
}
