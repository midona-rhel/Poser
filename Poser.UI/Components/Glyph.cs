namespace Poser.UI;

/// <summary>
/// A Tabler glyph on a logical square, tinted with currentColor — the sheet's
/// resolved foreground — exactly as CSS tints an inline SVG.
/// </summary>
public readonly record struct Glyph
{
    public TablerIcon? Icon { get; init; }

    /// <summary>The registry NAME, for the glyphs the enum does not
    /// carry. A compile-time literal, so naming one allocates nothing.</summary>
    internal string? Name { get; init; }

    /// <summary>Logical side; 0 takes the theme's icon size.</summary>
    public float Size { get; init; }

    /// <summary>Stroke in the icon's own 24-unit viewBox; 0 is the renderer's
    /// default. A small glyph needs a heavier one to read at all, and the
    /// weight is a property of how the icon is USED, not of the icon.</summary>
    public float Stroke { get; init; }

    public ElementSheet? Style { get; init; }

    /// <summary>Opts OUT of currentColor, for a control whose foreground is a
    /// compensated LABEL colour the glyph must not borrow.</summary>
    internal bool NoInherit { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Glyph glyph) => (UiNode)glyph;

    public static implicit operator UiNode(Glyph glyph) => new Element
    {
        Style = glyph.Style,
        Glyph = glyph.Icon,
        GlyphName = glyph.Name,
        GlyphSize = glyph.Size > 0f
            ? glyph.Size
            : Crystarium.ActiveTheme.Controls.IconSize,
        GlyphStroke = glyph.Stroke,
        GlyphNoInherit = glyph.NoInherit,
        Key = glyph.Key,
    };
}
