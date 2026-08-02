using System.Numerics;

namespace Poser.UI;

/// <summary>
/// The colour half of a sheet. Every field is optional: null is NOT a value,
/// it is "this sheet says nothing", and resolution falls through to the next
/// link of the chain.
///
/// <para><see cref="Foreground"/> is currentColor — it tints text AND glyphs
/// and INHERITS into the subtree. The two fades are different recipes and
/// therefore different fields: <see cref="Opacity"/> is the flat one a
/// translucent group wants, and <see cref="GroupOpacity"/> is the accepted
/// compensated recipe CSS <c>opacity</c> on a bordered control actually means
/// — the element flattens once and the label is pre-corrected to land where
/// the group would have.</para>
/// </summary>
public readonly record struct ColorSheet
{
    public Vector4? Fill { get; init; }

    public Vector4? Border { get; init; }

    /// <summary>currentColor: text, glyph tint, and what children inherit.</summary>
    public Vector4? Foreground { get; init; }

    /// <summary>Flat fade multiplied into the subtree's glyph opacity.</summary>
    public float? Opacity { get; init; }

    /// <summary>The compensated group fade. 1 is "no group".</summary>
    public float? GroupOpacity { get; init; }
}
