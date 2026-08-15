using System.Collections.Generic;
using System.Numerics;

namespace Poser.Domain.Presentation;

/// <summary>The three whole-model tint targets Poser owns. Props and
/// ornaments are deliberately absent — the references tint only these.</summary>
public enum PresentationModel
{
    Character,
    MainHand,
    OffHand,
}

/// <summary>The granular wet-surface state: weather wetness 0..1,
/// swimming wetness 0..1, and wetness depth 0..3. Glamourer's wetness is
/// binary; this exists only for the granular values.</summary>
public readonly record struct WetnessState(float Weather, float Swimming, float Depth);

/// <summary>
/// Everything Poser authored for one actor's runtime presentation. This
/// is the ONLY record of what must be undone; anything absent here was
/// never Poser's to restore. Not pose data, not a named layer, not
/// transform history, and never a second undo journal.
/// </summary>
public sealed record PresentationOverrides
{
    // ── Owned values ──────────────────────────────────────────────────
    public float? Opacity { get; init; }
    public IReadOnlyDictionary<PresentationModel, Vector4> Tints { get; init; } =
        new Dictionary<PresentationModel, Vector4>();
    /// <summary>The enabled wetness override; null while the game owns
    /// its own wetness.</summary>
    public WetnessState? Wetness { get; init; }

    // ── Captures ──────────────────────────────────────────────────────
    // Each is taken ONCE, before the first Poser edit of that field, and
    // is the only thing that can put that field back. A draw-object
    // replacement never re-captures: the replacement's temporary
    // defaults are not the actor's incoming state.

    public float? OpacityCapture { get; init; }
    public IReadOnlyDictionary<PresentationModel, Vector4> TintCaptures { get; init; } =
        new Dictionary<PresentationModel, Vector4>();
    /// <summary>The COMPLETE incoming three-float state, captured before
    /// the first wetness write; disabling restores all three.</summary>
    public WetnessState? WetnessCapture { get; init; }

    public static PresentationOverrides None { get; } = new();

    /// <summary>True when Poser owns anything that must be restored.</summary>
    public bool HasAny =>
        OpacityCapture != null || TintCaptures.Count > 0 || WetnessCapture != null;
}

/// <summary>One frame's live native read. Weapon tints are null when the
/// model is absent — an absent model is unavailable, never redirected.</summary>
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
