using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// CSS white-space policy for a wrapped run, covering the values Picto's
/// wrapping grammars use. <c>nowrap</c> is the Truncate constraint and
/// <c>pre</c> is a single unwrapped line — neither is a wrap policy.
/// </summary>
public enum TextWhitespace
{
    /// <summary>CSS <c>normal</c>: newlines, tabs, and space runs all
    /// collapse to single spaces; lines break at spaces.</summary>
    Normal,
    /// <summary>CSS <c>pre-line</c>: explicit newlines break; space and
    /// tab runs collapse to single spaces.</summary>
    PreLine,
    /// <summary>CSS <c>pre-wrap</c>: explicit newlines break; spaces and
    /// tabs are preserved (tabs advance to 8-space-width stops); lines
    /// still break at spaces, with break-point spaces hanging.</summary>
    PreWrap,
}

/// <summary>Horizontal alignment of a constrained run inside its box
/// (CSS <c>text-align</c>). Start draws from the box's start edge; End
/// pins the run's end to the end edge — a truncated run keeps its
/// ellipsis on that edge when truncation begins, and a raw overflow run
/// (narrower-than-ellipsis box) shows its END with the start clipped,
/// exactly as an end-aligned CSS line overflows. Center splits the
/// leftover width, going negative on overflow like End does.</summary>
public enum TextAlign
{
    Start,
    Center,
    End,
}

/// <summary>
/// A typed width constraint for a text run. Intrinsic text carries no
/// width; truncation REQUIRES one; wrapping requires one and owns its
/// optional CSS line-height and white-space policy. Constrained runs
/// carry a typed <see cref="TextAlign"/>, defaulting to Start. Invalid
/// combinations are unrepresentable and non-positive dimensions are
/// rejected at construction.
/// </summary>
public readonly struct TextConstraint
{
    internal enum FitMode { Intrinsic, Truncate, Wrap }

    internal FitMode Mode { get; }
    internal float Width { get; }
    internal float? LineHeight { get; }
    internal TextWhitespace Whitespace { get; }
    internal TextAlign Alignment { get; }

    private TextConstraint(
        FitMode mode, float width, float? lineHeight,
        TextWhitespace whitespace, TextAlign alignment)
    {
        Mode = mode;
        Width = width;
        LineHeight = lineHeight;
        Whitespace = whitespace;
        Alignment = alignment;
    }

    /// <summary>Natural content width; never cut.</summary>
    public static TextConstraint Intrinsic => default;

    /// <summary>One line, ellipsis-truncated and CLIPPED inside the pixel
    /// width (Picto's <c>overflow:hidden; text-overflow:ellipsis;
    /// white-space:nowrap</c> idiom). The run occupies the full width in
    /// layout, exactly like the CSS box, and aligns inside it per
    /// <paramref name="alignment"/>.</summary>
    public static TextConstraint Truncate(
        float width, TextAlign alignment = TextAlign.Start)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Truncation requires a positive pixel width.");
        return new TextConstraint(
            FitMode.Truncate, width, null, TextWhitespace.Normal, alignment);
    }

    /// <summary>
    /// Word wrap inside the pixel width. The run occupies the full width
    /// in layout, like the CSS box; a single over-wide word OVERFLOWS its
    /// line (CSS <c>overflow-wrap: normal</c>) rather than being
    /// hard-broken. Whitespace follows the typed <paramref name="whitespace"/>
    /// policy. The line advance is the FRACTIONAL CSS line height,
    /// accumulated unrounded so long paragraphs cannot drift; each line's
    /// glyph run sits half-leading-centered inside its explicit line box.
    /// A null line height uses the font's natural line box.
    /// </summary>
    public static TextConstraint Wrap(
        float width,
        float? lineHeight = null,
        TextWhitespace whitespace = TextWhitespace.Normal,
        TextAlign alignment = TextAlign.Start)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Wrapping requires a positive pixel width.");
        if (lineHeight is { } multiplier && !(multiplier > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(lineHeight), multiplier, "A line height must be positive.");
        return new TextConstraint(
            FitMode.Wrap, width, lineHeight, whitespace, alignment);
    }
}

/// <summary>
/// One text run's style, Picto typography semantics: token sizes
/// (shortcut 10 / caption 11 / label 12 / body 13 / surface title 14),
/// weights 400/500/600, theme text colors, the mono family for tabular
/// values, and the OPACITY disabled idiom — Picto dims disabled text by
/// opacity, it does not recolor it. Unset members resolve from the
/// active theme (body size, regular weight, primary text).
/// </summary>
public readonly record struct TextStyle
{
    /// <summary>CSS-pixel font size; null resolves Typography.BodySize.
    /// A non-positive size is rejected when the style resolves.</summary>
    public float? Size { get; init; }

    /// <summary>null resolves Regular.</summary>
    public FontWeight? Weight { get; init; }

    public FontFamily Family { get; init; }

    /// <summary>null resolves the theme's primary text color.</summary>
    public Vector4? Color { get; init; }

    /// <summary>Applies the disabled opacity to the resolved color.</summary>
    public bool Disabled { get; init; }
}
