using System.Numerics;

namespace Poser.UI;

/// <summary>
/// CSS-shaped style record. Every field is nullable; null = unspecified, falls
/// through the cascade. Resolved at render time by merging stylesheet rules,
/// state-based variants, inline overrides, parent-inherited values, and defaults.
/// </summary>
public record struct ElementStyle
{
    // ---- Box (non-inherited) ----
    public Sizing? Width;
    public Sizing? Height;
    public Spacing? Margin;
    public Spacing? Padding;
    public Vector4? BackgroundColor;
    public float?   BorderRadius;
    public float?   BorderWidth;
    public Vector4? BorderColor;
    public BoxShadow? BoxShadow;
    public bool? RaisedGradient;

    // ---- Layout (non-inherited) ----
    public FlexDirection? FlexDirection;
    public float? Gap;
    public Align? AlignItems;
    public Justify? JustifyContent;

    // ---- Inherited ----
    public Vector4? Color;
    public float?   Opacity;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public TextAlign? TextAlign;

    /// <summary>Per-field merge: each field of <paramref name="overlay"/> overwrites this if non-null.</summary>
    public ElementStyle MergedWith(in ElementStyle overlay)
    {
        var r = this;
        if (overlay.Width.HasValue)            r.Width = overlay.Width;
        if (overlay.Height.HasValue)           r.Height = overlay.Height;
        if (overlay.Margin.HasValue)           r.Margin = overlay.Margin;
        if (overlay.Padding.HasValue)          r.Padding = overlay.Padding;
        if (overlay.BackgroundColor.HasValue)  r.BackgroundColor = overlay.BackgroundColor;
        if (overlay.BorderRadius.HasValue)     r.BorderRadius = overlay.BorderRadius;
        if (overlay.BorderWidth.HasValue)      r.BorderWidth = overlay.BorderWidth;
        if (overlay.BorderColor.HasValue)      r.BorderColor = overlay.BorderColor;
        if (overlay.BoxShadow.HasValue)        r.BoxShadow = overlay.BoxShadow;
        if (overlay.RaisedGradient.HasValue)   r.RaisedGradient = overlay.RaisedGradient;
        if (overlay.FlexDirection.HasValue)    r.FlexDirection = overlay.FlexDirection;
        if (overlay.Gap.HasValue)              r.Gap = overlay.Gap;
        if (overlay.AlignItems.HasValue)       r.AlignItems = overlay.AlignItems;
        if (overlay.JustifyContent.HasValue)   r.JustifyContent = overlay.JustifyContent;
        if (overlay.Color.HasValue)            r.Color = overlay.Color;
        if (overlay.Opacity.HasValue)          r.Opacity = overlay.Opacity;
        if (overlay.FontFamily.HasValue)       r.FontFamily = overlay.FontFamily;
        if (overlay.FontSize.HasValue)         r.FontSize = overlay.FontSize;
        if (overlay.TextAlign.HasValue)        r.TextAlign = overlay.TextAlign;
        return r;
    }
}
