using System.Numerics;

namespace Poser.UI;

/// <summary>
/// The single resolution path. A null field is not part of a spec, so every
/// property falls through inline patch → active state look → family sheet →
/// inherited context → renderer default, and the chain is FLATTENED into one
/// plain struct rather than merged into a new sheet.
///
/// <para>State looks CASCADE in precedence order — Selected, then Hover, then
/// Active, then Disabled — so a row that is both selected and pressed keeps
/// the fill it stated for either, and a single-winner rule never has to be
/// worked around by restating values.</para>
/// </summary>
internal static class StyleResolver
{
    internal static ResolvedLayout Layout(SheetRef sheet, in ElementSheet? patch)
    {
        ResolvedLayout resolved = default;
        Apply(ref resolved, ThemeStyles.Of(sheet).Layout);
        if (patch is { Layout: { } inline })
            Apply(ref resolved, inline);
        return resolved;
    }

    /// <summary>Resolved during measure: a run's intrinsic box is made of it,
    /// and no pseudo state can reach it.</summary>
    internal static ResolvedType Type(
        SheetRef sheet, in ElementSheet? patch, in InheritedType inherited)
    {
        ResolvedType resolved = new()
        {
            Size = inherited.Size,
            Font = inherited.Font,
            Weight = inherited.Weight,
        };
        Apply(ref resolved, ThemeStyles.Of(sheet).Type);
        if (patch is { Type: { } inline })
            Apply(ref resolved, inline);
        return resolved;
    }

    internal static ResolvedPaint Paint(
        SheetRef sheet,
        in ElementSheet? patch,
        bool hovered,
        bool active,
        bool disabled,
        bool selected,
        Vector4? inheritedForeground)
    {
        ResolvedPaint resolved = default;
        resolved.Opacity = 1f;
        resolved.Foreground = inheritedForeground;

        ref readonly ElementSheet family = ref ThemeStyles.Of(sheet);
        ApplyBase(ref resolved, in family);
        if (patch is { } inline)
            ApplyBase(ref resolved, in inline);
        if (selected)
            ApplyLook(ref resolved, family.Selected, patch?.Selected);

        // The cascade only ever DIVERGES at the hover step, so the two fill
        // endpoints are one branch rather than two whole resolutions.
        ResolvedPaint rest = resolved;
        Tail(ref rest, in family, patch, active, disabled);
        ApplyLook(ref resolved, family.Hover, patch?.Hover);
        Tail(ref resolved, in family, patch, active, disabled);

        Vector4? hoverFill = resolved.Fill;
        if (!hovered)
            resolved = rest;
        resolved.RestFill = rest.Fill;
        resolved.HoverFill = hoverFill;
        return resolved;
    }

    private static void Tail(
        ref ResolvedPaint resolved,
        in ElementSheet family,
        in ElementSheet? patch,
        bool active,
        bool disabled)
    {
        if (active)
            ApplyLook(ref resolved, family.Active, patch?.Active);
        if (disabled)
            ApplyLook(ref resolved, family.Disabled, patch?.Disabled);
    }

    private static void ApplyLook(
        ref ResolvedPaint resolved, in LookSheet? family, in LookSheet? inline)
    {
        if (family is { } f)
            ApplyLook(ref resolved, in f);
        if (inline is { } i)
            ApplyLook(ref resolved, in i);
    }

    private static void ApplyLook(ref ResolvedPaint resolved, in LookSheet look)
    {
        if (look.Colors is { } colors)
            Apply(ref resolved, in colors);
        if (look.Shape is { } shape)
            Apply(ref resolved, in shape);
        if (look.Motion is { Fill: { } fill })
            resolved.FillTransition = fill;
    }

    private static void ApplyBase(ref ResolvedPaint resolved, in ElementSheet sheet)
    {
        if (sheet.Colors is { } colors)
            Apply(ref resolved, in colors);
        if (sheet.Shape is { } shape)
            Apply(ref resolved, in shape);
        if (sheet.Motion is { Fill: { } fill })
            resolved.FillTransition = fill;
    }

    private static void Apply(ref ResolvedPaint resolved, in ColorSheet colors)
    {
        if (colors.Fill is { } fill)
            resolved.Fill = fill;
        if (colors.Border is { } border)
            resolved.Border = border;
        if (colors.Foreground is { } foreground)
            resolved.Foreground = foreground;
        if (colors.Opacity is { } opacity)
            resolved.Opacity = opacity;
        if (colors.GroupOpacity is { } group)
            resolved.GroupOpacity = group;
    }

    private static void Apply(ref ResolvedPaint resolved, in ShapeSheet shape)
    {
        if (shape.Radius is { } radius)
            resolved.Radius = radius;
        if (shape.BorderWidth is { } width)
            resolved.BorderWidth = width;
    }

    private static void Apply(ref ResolvedType resolved, in TypographySheet? sheet)
    {
        if (sheet is { } type)
            Apply(ref resolved, type);
    }

    private static void Apply(ref ResolvedType resolved, in TypographySheet type)
    {
        if (type.FontSize is { } size)
            resolved.Size = size;
        if (type.Font is { } font)
            resolved.Font = font;
        if (type.Weight is { } weight)
            resolved.Weight = weight;
        if (type.Overflow is { } overflow)
            resolved.Overflow = overflow;
    }

    private static void Apply(ref ResolvedLayout resolved, in LayoutSheet? sheet)
    {
        if (sheet is { } layout)
            Apply(ref resolved, layout);
    }

    private static void Apply(ref ResolvedLayout resolved, in LayoutSheet layout)
    {
        if (layout.Flow is { } flow)
            resolved.Flow = flow;
        if (layout.Padding is { } padding)
            resolved.Padding = padding;
        if (layout.Margin is { } margin)
            resolved.Margin = margin;
        if (layout.Gap is { } gap)
            resolved.Gap = gap;
        if (layout.Width is { } width)
            resolved.Width = width;
        if (layout.Height is { } height)
            resolved.Height = height;
        if (layout.MaxWidth is { } maxWidth)
            resolved.MaxWidth = maxWidth;
        if (layout.Align is { } align)
            resolved.Align = align;
        if (layout.Justify is { } justify)
            resolved.Justify = justify;
    }
}
