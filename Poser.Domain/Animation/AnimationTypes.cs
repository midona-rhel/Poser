using System;
using System.Collections.Generic;

namespace Poser.Domain.Animation;

/// <summary>Native animation slots.</summary>
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

/// <summary>
/// Source of an animation entry, used to choose how the game plays it.
/// </summary>
public enum AnimationKind
{
    Action,
    Emote,
    Expression,
    RawTimeline,
}

/// <summary>
/// Native pose-mode values. The stance picker exposes its four selectable
/// families; the remaining values preserve exact live read-back.
/// </summary>
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

    /// <summary>
    /// Layers available in the animation picker.
    /// </summary>
    public static IReadOnlyList<AnimationSlot> Selectable { get; } = new[]
    {
        AnimationSlot.Base,
        AnimationSlot.UpperBody,
        AnimationSlot.Facial,
        AnimationSlot.Additive,
        AnimationSlot.Lips,
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
    /// <summary>The idle timeline.</summary>
    public const ushort Idle = 3;
    /// <summary>The neutral facial timeline.</summary>
    public const ushort StraightFace = 604;
    public const ushort DrawWeapon = 1;
    public const ushort SheatheWeapon = 2;
    /// <summary>Battle idle, used when the weapon is drawn.</summary>
    public const ushort BattleIdle = 34;
    /// <summary>First and last speech timeline offered as a lip override.</summary>
    public const ushort FirstLips = 0x272;
    public const ushort LastLips = 0x272 + 8;
}

/// <summary>
/// Animation picker entry with game IDs and searchable display information.
/// </summary>
public sealed record TimelineEntry(
    uint TimelineId,
    string Name,
    AnimationKind Kind,
    AnimationSlot Slot,
    uint Icon = 0,
    uint EmoteId = 0,
    int EmoteIndex = -1,
    bool? DrawsWeapon = null,
    string? Key = null)
{
    /// <summary>
    /// Whether Poser can request this emote's intro followed by its loop.
    /// </summary>
    public bool CanPlayFromStart => Kind is AnimationKind.Emote or AnimationKind.Expression &&
        EmoteIndex == 0 && EmoteId != 0;
}

/// <summary>
/// Saved state of an animation layer. Applied is the animation Poser started;
/// Live says whether it is still playing. HeldSeconds records a paused position;
/// running animations are restored from their start.
/// </summary>
public readonly record struct LayerSnapshot(
    ushort? Applied, bool Live, bool Loop, float? Speed, float Weight, float? HeldSeconds, float? EndedSeconds);

/// <summary>
/// An actor's whole animation state, as an undo/redo step stores it: every
/// layer, the whole-actor speed (0 = paused), the held expression, lips, stance,
/// weapon, position lock and the advanced mode.
/// </summary>
public sealed record AnimationSnapshot(
    LayerSnapshot Base, LayerSnapshot UpperBody, LayerSnapshot Facial, LayerSnapshot Additive,
    ushort? Lips, float? OverallSpeed, ushort? HeldExpression,
    AnimationStance Stance, int Pose, bool WeaponDrawn, bool PositionLock, bool Advanced)
{
    public static readonly AnimationSlot[] Layers =
        [AnimationSlot.Base, AnimationSlot.UpperBody, AnimationSlot.Facial, AnimationSlot.Additive];

    public LayerSnapshot For(AnimationSlot slot) => slot switch
    {
        AnimationSlot.Base => Base,
        AnimationSlot.UpperBody => UpperBody,
        AnimationSlot.Facial => Facial,
        AnimationSlot.Additive => Additive,
        _ => default,
    };
}

/// <summary>Native base state captured before Poser's first write.</summary>
public readonly record struct BaseAnimationCapture(
    byte Mode,
    byte ModeParam,
    ushort BaseTimeline,
    ushort BaseSlotTimeline = 0);

/// <summary>
/// Indices identifying an animation control. Callers must also check the
/// skeleton version before using them, since a replacement can reuse the indices.
/// </summary>
public readonly record struct ScrubControlId(int Partial, int Control)
{
    /// <summary>A layer scrubbed by its slot timeline's own clock (no fixed
    /// Havok control): <see cref="Control"/> is the slot.</summary>
    public static ScrubControlId Timeline(AnimationSlot slot) => new(TimelinePartial, (int)slot);
    public const int TimelinePartial = -1;
    public bool IsTimeline => Partial == TimelinePartial;
    public override string ToString() => IsTimeline ? $"tl.{Control}" : $"{Partial}.{Control}";
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
/// Animation values read from the game for one frame.
/// These are observations, separate from the overrides Poser records for restoration.
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
/// Stance and pose index captured before Poser changes them.
/// </summary>
public readonly record struct StanceCapture(AnimationStance Stance, int Pose);

public sealed record AnimationOverrides
{
    public ushort? BaseTimeline { get; init; }
    /// <summary>Explicit non-base timeline selections.</summary>
    public IReadOnlyDictionary<AnimationSlot, ushort> SelectedSlots { get; init; } =
        new Dictionary<AnimationSlot, ushort>();
    /// <summary>Last timeline successfully applied to each non-base slot.</summary>
    public IReadOnlyDictionary<AnimationSlot, ushort> AppliedSlots { get; init; } =
        new Dictionary<AnimationSlot, ushort>();
    public float? OverallSpeed { get; init; }
    /// <summary>
    /// Layers whose animations Poser is currently set up to repeat.
    /// </summary>
    public IReadOnlyDictionary<AnimationSlot, ushort> LoopedSlots { get; init; } =
        new Dictionary<AnimationSlot, ushort>();
    public IReadOnlySet<AnimationSlot> LoopWantedSlots { get; init; } =
        new HashSet<AnimationSlot>();
    /// <summary>Original non-base timelines.</summary>
    public IReadOnlyDictionary<AnimationSlot, ushort> SlotCaptures { get; init; } =
        new Dictionary<AnimationSlot, ushort>();
    public IReadOnlyDictionary<AnimationSlot, float> SlotSpeeds { get; init; } =
        new Dictionary<AnimationSlot, float>();
    /// <summary>Native speed before Poser's first override per slot.</summary>
    public IReadOnlyDictionary<AnimationSlot, float> SlotSpeedCaptures { get; init; } =
        new Dictionary<AnimationSlot, float>();
    /// <summary>Per-layer blend weight in [0, 1]; absent means the game's 1.</summary>
    public IReadOnlyDictionary<AnimationSlot, float> SlotWeights { get; init; } =
        new Dictionary<AnimationSlot, float>();
    /// <summary>Last nonzero speed restored by the layer Play action.</summary>
    public IReadOnlyDictionary<AnimationSlot, float> SlotResumeSpeeds { get; init; } =
        new Dictionary<AnimationSlot, float>();
    public ushort? Lips { get; init; }
    public bool PositionLock { get; init; }

    /// <summary>Original base animation state.</summary>
    public BaseAnimationCapture? BaseCapture { get; init; }
    /// <summary>The held facial expression.</summary>
    public ushort? HeldExpression { get; init; }
    /// <summary>
    /// Finished animations and their durations, kept so Play or scrubbing can restart them.
    /// </summary>
    public IReadOnlyDictionary<AnimationSlot, float> EndedSlots { get; init; } = new Dictionary<AnimationSlot, float>();
    /// <summary>Lips timeline before the first override. Selecting None
    /// restores this value rather than writing native "no speech" zero.</summary>
    public ushort? LipsCapture { get; init; }
    /// <summary>Stance family and pose index before the first stance change.</summary>
    public StanceCapture? StanceCaptureValue { get; init; }
    /// <summary>Weapon state before Poser first drew or sheathed.</summary>
    public bool? WeaponCapture { get; init; }

    public static AnimationOverrides None { get; } = new();

    /// <summary>True when Poser owns anything that must be restored.</summary>
    public bool HasAny =>
        BaseCapture != null || LipsCapture != null || OverallSpeed != null ||
        PositionLock || SlotSpeeds.Count > 0 || SlotWeights.Count > 0 || HeldExpression != null ||
        SelectedSlots.Count > 0 || AppliedSlots.Count > 0 ||
        SlotSpeedCaptures.Count > 0 || LoopedSlots.Count > 0 ||
        LoopWantedSlots.Count > 0 || SlotCaptures.Count > 0 ||
        StanceCaptureValue != null || WeaponCapture != null;

    public bool IsPaused => OverallSpeed is 0f;
}
