using System.Collections.Generic;
using System.Numerics;

namespace Poser.Domain.Presentation;

/// <summary>
/// Models supported by the whole-model tint control.
/// </summary>
public enum PresentationModel
{
    Character,
    MainHand,
    OffHand,
}

/// <summary>
/// Weather and swimming wetness values (0–1), plus wetness depth (0–3).
/// </summary>
public readonly record struct WetnessState(float Weather, float Swimming, float Depth);

/// <summary>
/// Runtime appearance edits and the original values captured for restoration.
/// Pose and transform edits are stored separately.
/// </summary>
public sealed record PresentationOverrides
{
    // ── Owned values ──────────────────────────────────────────────────
    public float? Opacity { get; init; }
    public IReadOnlyDictionary<PresentationModel, Vector4> Tints { get; init; } =
        new Dictionary<PresentationModel, Vector4>();
    /// <summary>
    /// Wetness override, or null to leave wetness under game control.
    /// </summary>
    public WetnessState? Wetness { get; init; }
    public IReadOnlyDictionary<AppearanceColorChannel, Vector4> Colors { get; init; } = new Dictionary<AppearanceColorChannel, Vector4>();
    public IReadOnlyDictionary<AppearanceColorChannel, Vector4> ColorCaptures { get; init; } = new Dictionary<AppearanceColorChannel, Vector4>();

    // ── Captures ──────────────────────────────────────────────────────
    // Keep the pre-edit values across redraws. A newly created draw object
    // may contain temporary defaults rather than the values being restored.

    public float? OpacityCapture { get; init; }
    public IReadOnlyDictionary<PresentationModel, Vector4> TintCaptures { get; init; } =
        new Dictionary<PresentationModel, Vector4>();
    /// <summary>
    /// All three wetness values captured before the first override.
    /// </summary>
    public WetnessState? WetnessCapture { get; init; }

    public static PresentationOverrides None { get; } = new();

    /// <summary>
    /// Whether any original values have been captured for later restoration.
    /// </summary>
    public bool HasAny =>
        OpacityCapture != null || TintCaptures.Count > 0 || WetnessCapture != null || ColorCaptures.Count > 0;
}

/// <summary>
/// Snapshot of rendered appearance values. A null weapon tint means that model was unavailable.
/// </summary>
public sealed record PresentationReading(
    float Opacity,
    Vector4? CharacterTint,
    Vector4? MainHandTint,
    Vector4? OffHandTint,
    WetnessState Wetness)
{
    public Vector4? TintFor(PresentationModel model) => model switch
    {
        PresentationModel.Character => CharacterTint,
        PresentationModel.MainHand => MainHandTint,
        _ => OffHandTint,
    };
}
