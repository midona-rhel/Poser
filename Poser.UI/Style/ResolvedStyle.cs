using System.Numerics;

namespace Poser.UI;

/// <summary>
/// One element's flattened box description. Produced once per element per
/// frame by <see cref="StyleResolver"/>; every field is a VALUE, so the
/// solver never walks a chain and never merges a sheet.
/// </summary>
internal struct ResolvedLayout
{
    internal UiFlow Flow;
    internal EdgeInsets Padding;
    internal EdgeInsets Margin;
    internal float Gap;
    internal UiDim Width;
    internal UiDim Height;
    internal float MaxWidth;
    internal UiAlign Align;
    internal UiAlign Justify;
}

/// <summary>
/// One element's flattened typography, resolved during MEASURE because it is
/// what a run's intrinsic box is made of. Pseudo states cannot reach it — a
/// <see cref="LookSheet"/> has no typography member — so one resolution serves
/// measure, arrange and paint alike.
/// </summary>
internal struct ResolvedType
{
    internal float? Size;
    internal FontFamily Font;
    internal FontWeight? Weight;
    internal TextOverflow Overflow;

    /// <summary>The style a run measures and draws with. An unstated size or
    /// weight resolves inside the renderer, which keeps ONE default per token.
    /// </summary>
    internal readonly TextStyle Text(Vector4? color) => new()
    {
        Size = Size,
        Family = Font,
        Weight = Weight,
        Color = color,
    };
}

/// <summary>
/// One element's flattened paint description at its CURRENT state.
/// </summary>
internal struct ResolvedPaint
{
    internal Vector4? Fill;

    /// <summary>The same resolution with the hover look withheld, and with it
    /// forced on. A ramp interpolates between the two ENDPOINTS rather than
    /// between "now" and "then", which is what lets a pointer that leaves
    /// mid-flight retrace the distance it already covered.</summary>
    internal Vector4? RestFill;

    /// <inheritdoc cref="RestFill"/>
    internal Vector4? HoverFill;

    internal Vector4? Border;

    /// <summary>currentColor: the label's tint, the glyph's tint, and what
    /// the subtree inherits.</summary>
    internal Vector4? Foreground;

    /// <summary>The element's OWN flat fade; the walk composes it onto what
    /// the subtree already carried.</summary>
    internal float Opacity;

    /// <summary>The compensated group fade, or null for "no group".</summary>
    internal float? GroupOpacity;

    internal float Radius;
    internal float BorderWidth;
    internal Transition? FillTransition;
}

/// <summary>
/// What a subtree inherits at MEASURE time. Box, spacing and size deliberately
/// do not inherit, and colour cannot change a box, so only typography travels
/// here.
/// </summary>
internal readonly struct InheritedType
{
    internal InheritedType(float? size, FontFamily font, FontWeight? weight)
    {
        Size = size;
        Font = font;
        Weight = weight;
    }

    internal readonly float? Size;
    internal readonly FontFamily Font;
    internal readonly FontWeight? Weight;

    internal static InheritedType Root => default;
}
