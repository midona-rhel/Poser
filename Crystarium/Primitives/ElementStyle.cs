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
    public Sizing? MinWidth;
    public Sizing? MaxWidth;
    public Sizing? MinHeight;
    public Sizing? MaxHeight;
    public Spacing? Margin;
    public Spacing? Padding;
    public Vector4? BackgroundColor;
    public float?   BorderRadius;
    public float?   BorderWidth;
    public Vector4? BorderColor;
    public BoxShadow? BoxShadow;
    public BoxShadow[]? BoxShadows;
    public Gradient? BackgroundGradient;
    public Outline? Outline;
    public bool? RaisedGradient;
    public float? AspectRatio;
    public Cursor? Cursor;
    public Transition? Transition;

    // ---- Display & flow ----
    public Display? Display;

    // ---- Overflow ----
    public Overflow? Overflow;

    // ---- Positioning ----
    public Position? Position;
    public float? Top;
    public float? Right;
    public float? Bottom;
    public float? Left;

    // ---- Layout (non-inherited) ----
    public FlexDirection? FlexDirection;
    public FlexWrap? FlexWrap;
    public float? Gap;
    public float? RowGap;     // line spacing when FlexWrap.Wrap creates extra lines
    public Align? AlignItems;
    public Justify? JustifyContent;
    public AlignSelf? AlignSelf;

    // Grid (display: grid)
    public Sizing[]? GridTemplateColumns;
    public float? GridColumnGap;
    public float? GridRowGap;
    public int? GridColumn;
    public int? GridRow;
    public int? GridColumnSpan;
    public int? GridRowSpan;

    // ---- Inherited ----
    public Vector4? Color;
    public float?   Opacity;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public TextAlign? TextAlign;
    public TextOverflow? TextOverflow;
    public WhiteSpace?   WhiteSpace;
    public float?        LineHeight;
    public float?        LetterSpacing;
    public TextShadow?   TextShadow;

    /// <summary>Per-field merge: each field of <paramref name="overlay"/> overwrites this if non-null.</summary>
    public ElementStyle MergedWith(in ElementStyle overlay)
    {
        var r = this;
        if (overlay.Width.HasValue)            r.Width = overlay.Width;
        if (overlay.Height.HasValue)           r.Height = overlay.Height;
        if (overlay.MinWidth.HasValue)         r.MinWidth = overlay.MinWidth;
        if (overlay.MaxWidth.HasValue)         r.MaxWidth = overlay.MaxWidth;
        if (overlay.MinHeight.HasValue)        r.MinHeight = overlay.MinHeight;
        if (overlay.MaxHeight.HasValue)        r.MaxHeight = overlay.MaxHeight;
        if (overlay.Margin.HasValue)           r.Margin = overlay.Margin;
        if (overlay.Padding.HasValue)          r.Padding = overlay.Padding;
        if (overlay.BackgroundColor.HasValue)  r.BackgroundColor = overlay.BackgroundColor;
        if (overlay.BorderRadius.HasValue)     r.BorderRadius = overlay.BorderRadius;
        if (overlay.BorderWidth.HasValue)      r.BorderWidth = overlay.BorderWidth;
        if (overlay.BorderColor.HasValue)      r.BorderColor = overlay.BorderColor;
        if (overlay.BoxShadow.HasValue)        r.BoxShadow = overlay.BoxShadow;
        if (overlay.BoxShadows != null)        r.BoxShadows = overlay.BoxShadows;
        if (overlay.BackgroundGradient.HasValue) r.BackgroundGradient = overlay.BackgroundGradient;
        if (overlay.Outline.HasValue)          r.Outline = overlay.Outline;
        if (overlay.RaisedGradient.HasValue)   r.RaisedGradient = overlay.RaisedGradient;
        if (overlay.AspectRatio.HasValue)      r.AspectRatio = overlay.AspectRatio;
        if (overlay.Cursor.HasValue)           r.Cursor = overlay.Cursor;
        if (overlay.Transition.HasValue)       r.Transition = overlay.Transition;
        if (overlay.Display.HasValue)          r.Display = overlay.Display;
        if (overlay.Overflow.HasValue)         r.Overflow = overlay.Overflow;
        if (overlay.Position.HasValue)         r.Position = overlay.Position;
        if (overlay.Top.HasValue)              r.Top = overlay.Top;
        if (overlay.Right.HasValue)            r.Right = overlay.Right;
        if (overlay.Bottom.HasValue)           r.Bottom = overlay.Bottom;
        if (overlay.Left.HasValue)             r.Left = overlay.Left;
        if (overlay.FlexDirection.HasValue)    r.FlexDirection = overlay.FlexDirection;
        if (overlay.FlexWrap.HasValue)         r.FlexWrap = overlay.FlexWrap;
        if (overlay.Gap.HasValue)              r.Gap = overlay.Gap;
        if (overlay.RowGap.HasValue)           r.RowGap = overlay.RowGap;
        if (overlay.AlignItems.HasValue)       r.AlignItems = overlay.AlignItems;
        if (overlay.JustifyContent.HasValue)   r.JustifyContent = overlay.JustifyContent;
        if (overlay.AlignSelf.HasValue)        r.AlignSelf = overlay.AlignSelf;
        if (overlay.GridTemplateColumns != null) r.GridTemplateColumns = overlay.GridTemplateColumns;
        if (overlay.GridColumnGap.HasValue)    r.GridColumnGap = overlay.GridColumnGap;
        if (overlay.GridRowGap.HasValue)       r.GridRowGap = overlay.GridRowGap;
        if (overlay.GridColumn.HasValue)       r.GridColumn = overlay.GridColumn;
        if (overlay.GridRow.HasValue)          r.GridRow = overlay.GridRow;
        if (overlay.GridColumnSpan.HasValue)   r.GridColumnSpan = overlay.GridColumnSpan;
        if (overlay.GridRowSpan.HasValue)      r.GridRowSpan = overlay.GridRowSpan;
        if (overlay.Color.HasValue)            r.Color = overlay.Color;
        if (overlay.Opacity.HasValue)          r.Opacity = overlay.Opacity;
        if (overlay.FontFamily.HasValue)       r.FontFamily = overlay.FontFamily;
        if (overlay.FontSize.HasValue)         r.FontSize = overlay.FontSize;
        if (overlay.TextAlign.HasValue)        r.TextAlign = overlay.TextAlign;
        if (overlay.TextOverflow.HasValue)     r.TextOverflow = overlay.TextOverflow;
        if (overlay.WhiteSpace.HasValue)       r.WhiteSpace = overlay.WhiteSpace;
        if (overlay.LineHeight.HasValue)       r.LineHeight = overlay.LineHeight;
        if (overlay.LetterSpacing.HasValue)    r.LetterSpacing = overlay.LetterSpacing;
        if (overlay.TextShadow.HasValue)       r.TextShadow = overlay.TextShadow;
        return r;
    }
}
