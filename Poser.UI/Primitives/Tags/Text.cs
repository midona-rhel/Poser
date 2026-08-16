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

public static partial class Crystarium
{
    /// <summary>Plain inline body text from the active theme.</summary>
    public static void Text(string text)
        => Text(text, default, TextConstraint.Intrinsic);

    /// <summary>Inline (cursor-flow) text at its intrinsic width.</summary>
    public static void Text(string text, in TextStyle style)
        => Text(text, style, TextConstraint.Intrinsic);

    /// <summary>Inline (cursor-flow) text under a typed constraint. A
    /// constrained run occupies <c>constraint.Width</c> in layout, so
    /// following items flow from the constraint edge exactly like
    /// siblings of the CSS box.</summary>
    public static void Text(string text, in TextStyle style, TextConstraint constraint)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = DrawTextRun(origin, text, style, constraint, measure: true);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>Screen-positioned text for composed chrome and canvases.
    /// Draws into the current window's draw list and submits NO layout
    /// item.</summary>
    public static void TextAt(Vector2 position, string text, in TextStyle style)
        => DrawTextRun(position, text, style, TextConstraint.Intrinsic);

    /// <summary>Screen-positioned text under a typed constraint.</summary>
    public static void TextAt(
        Vector2 position, string text, in TextStyle style, TextConstraint constraint)
        => DrawTextRun(position, text, style, constraint);

    /// <summary>
    /// Extra lift, in CSS px, for a run seated beside an icon: the eye
    /// judges that run against the ICON's ink centroid, not the band
    /// centre. The value is constrained: with the metric ink rise it must
    /// land BOTH accepted seats — the tree row and the context menu row —
    /// on their measured pixel exactly, and it is the only such bias.
    /// </summary>
    public const float IconAdjacentInkBias = -1.5f;

    /// <summary>Snaps an ink-centered baseline seat, breaking ties toward
    /// the SMALLER y. A tie resolved low reproduces the exact defect
    /// <see cref="TextInBand"/> exists to kill.</summary>
    private static float InkSnapY(float y) => MathF.Ceiling(y - 0.5f);

    /// <summary>
    /// The ink-seated y <see cref="TextInBand"/> applies, for the sites
    /// that cannot route through it because their glyphs land on a
    /// different draw list (the foreground-composited hover card). Same
    /// metrics, same snap, same <paramref name="besideIcon"/> bias — a
    /// caller seats its own run at this y and nowhere else.
    /// </summary>
    internal static float InkSeatY(
        float bandMinY, float bandHeight, float measuredHeight,
        in TextStyle style, bool besideIcon = false)
    {
        ref readonly var theme = ref ActiveThemeRef;
        float size = style.Size ?? theme.Typography.BodySize;
        float rise = FontRegistry.InkRise(
            style.Family, style.Weight ?? FontWeight.Regular, size);
        if (besideIcon)
            rise += IconAdjacentInkBias;
        return InkSnapY(
            bandMinY + (bandHeight - measuredHeight) * 0.5f
            + rise * ImGuiHelpers.GlobalScale);
    }

    /// <summary>
    /// THE way to center one line of text in a band. Vertically it seats
    /// the INK — the cap-to-baseline band — on the band's midline, using
    /// the face's real metrics (<see cref="FontRegistry.InkRise"/>) instead
    /// of a per-surface tuned constant; line-box centering alone reads low
    /// because internal leading is asymmetric. Two modes: the default
    /// band-centered seat, and <paramref name="besideIcon"/>, which adds
    /// <see cref="IconAdjacentInkBias"/>. Horizontally the run's box is
    /// placed by <paramref name="align"/>.
    /// </summary>
    public static void TextInBand(
        Vector2 bandMin, Vector2 bandSize, string text, in TextStyle style,
        TextAlign align = TextAlign.Start, bool besideIcon = false)
        => TextInBand(
            bandMin, bandSize, text, style,
            TextConstraint.Intrinsic, align, besideIcon);

    /// <summary>Band-centered text under a typed constraint. The
    /// constraint's own width is the box aligned inside the band, and the
    /// constraint's own alignment governs the run inside that box.</summary>
    public static void TextInBand(
        Vector2 bandMin, Vector2 bandSize, string text, in TextStyle style,
        TextConstraint constraint, TextAlign align = TextAlign.Start,
        bool besideIcon = false)
    {
        var measured = MeasureText(text, style);
        float boxWidth = constraint.Mode == TextConstraint.FitMode.Intrinsic
            ? measured.X
            : constraint.Width;
        // Y is snapped inside InkSeatY, so the tie policy applies; the
        // renderer's Optical.Snap then rounds an already-whole y to
        // itself and keeps owning X. Optical.Snap is unchanged globally.
        TextAt(
            new Vector2(
                bandMin.X + AlignOffset(align, bandSize.X, boxWidth),
                InkSeatY(bandMin.Y, bandSize.Y, measured.Y, style, besideIcon)),
            text, style, constraint);
    }

    /// <summary>Measures the run at its intrinsic size.</summary>
    public static Vector2 MeasureText(string text, in TextStyle style)
    {
        var (font, pushed, _, _) = ResolveStyle(style);
        try
        {
            return ImGui.CalcTextSize(Presentation(text));
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    /// <summary>
    /// Composition-internal ellipsis fitting for labels that a composed
    /// control renders itself (button captions). Everything else goes
    /// through the canonical CLIPPED renderer — this helper must never
    /// substitute for it, because when even the ellipsis cannot fit the
    /// result is the ORIGINAL run and only the renderer's clip makes
    /// that correct. Grapheme clusters never split; the result is
    /// presentation output and the caller's string is untouched.
    /// </summary>
    internal static string TruncateText(string text, in TextStyle style, float width)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Truncation requires a positive pixel width.");
        var (font, pushed, _, _) = ResolveStyle(style);
        try
        {
            return TruncateResolved(Presentation(text), width);
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    /// <summary>
    /// Resolves ONCE what a <see cref="TextConstraint.Truncate"/> run of
    /// this width will draw, so a surface that restates the same run every
    /// frame can memoize the answer instead of re-shaping and re-allocating
    /// it. Null means the run already fits and the caller draws it
    /// unconstrained, exactly as an on-the-spot fit would have. Otherwise
    /// the fitted presentation string, which the caller MUST draw back
    /// through <c>TextConstraint.Truncate</c> AT THE SAME WIDTH: the clip
    /// is what makes the unfittable case correct, and re-stating an
    /// already-fitted run costs the renderer no allocation and lands the
    /// same pixels. The answer holds only while the text, the width and
    /// the resolved face all hold — a caller that memoizes it owns that
    /// invalidation.
    /// </summary>
    public static string? FitTruncated(string text, in TextStyle style, float width)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Truncation requires a positive pixel width.");
        var (font, pushed, _, _) = ResolveStyle(style);
        try
        {
            string presented = Presentation(text);
            // The fit decision is the QUANTIZED one on purpose: it selects
            // between two renderer paths whose horizontal placement differs
            // inside the rounding slack, so it has to be the same
            // comparison MeasureText makes.
            return ImGui.CalcTextSize(presented).X <= width
                ? null
                : TruncateResolved(presented, width);
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    /// <summary>Presentation normalization: newlines canonicalized (CRLF
    /// and lone CR become LF, as the HTML parser does before layout) and
    /// composed NFC form, so measurement, truncation, wrapping, and
    /// drawing all see the same sequence the reference renderer shapes.
    /// Semantic content outside presentation is never rewritten.</summary>
    private static string Presentation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.Contains('\r'))
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return text.IsNormalized(NormalizationForm.FormC)
            ? text
            : text.Normalize(NormalizationForm.FormC);
    }

    private static (IFontHandle? Font, bool Pushed, float Size, Vector4 Color)
        ResolveStyle(in TextStyle style)
    {
        ref readonly var theme = ref ActiveThemeRef;
        if (style.Size is { } requested && !(requested > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(style), requested, "A font size must be positive.");
        float size = style.Size ?? theme.Typography.BodySize;
        var weight = style.Weight ?? FontWeight.Regular;
        var color = style.Color ?? theme.Text;
        if (style.Disabled)
            color = color.Fade(theme.Chrome.DisabledOpacity);
        var font = FontRegistry.Resolve(style.Family, weight, size);
        bool pushed = font is { Available: true };
        if (pushed)
            font!.Push();
        return (font, pushed, size, color);
    }

    /// <summary>The one text renderer: resolves the style, fits the run,
    /// snaps the origin to whole pixels, and draws through the window
    /// draw list. Returns the LAYOUT size — a constrained run occupies
    /// its constraint width regardless of ink. An INTRINSIC run has to be
    /// shaped a second time to state its size, so that measure is taken
    /// only when <paramref name="measure"/> says a caller consumes it;
    /// the screen-positioned entry points submit no layout item and
    /// discarded it every frame.</summary>
    private static Vector2 DrawTextRun(
        Vector2 position, string text, in TextStyle style, TextConstraint constraint,
        bool measure = false)
    {
        var (font, pushed, size, color) = ResolveStyle(style);
        try
        {
            text = Presentation(text);
            var dl = ImGui.GetWindowDrawList();
            uint packed = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
            var origin = ActiveThemeRef.Optical.Snap(position);
            switch (constraint.Mode)
            {
                case TextConstraint.FitMode.Truncate:
                {
                    // CSS overflow:hidden + text-overflow:ellipsis — the
                    // fitted string decides where the ellipsis goes, the
                    // clip owns the visual edge. When even the ellipsis
                    // cannot fit, the fitted string IS the original run
                    // and the clip cuts it raw, as Blink does. End
                    // alignment offsets by the leftover width, which for
                    // a raw overflow goes negative so the run's END stays
                    // at the box edge with the start clipped.
                    float natural = ImGui.GetTextLineHeight();
                    string fitted = TruncateResolved(text, constraint.Width);
                    float offset = AlignOffset(
                        constraint.Alignment,
                        constraint.Width,
                        FractionalTextWidth(fitted));
                    dl.PushClipRect(
                        origin,
                        origin + new Vector2(constraint.Width, natural),
                        true);
                    try
                    {
                        dl.AddText(
                            new Vector2(
                                MathF.Round(origin.X + offset), origin.Y),
                            packed, fitted);
                    }
                    finally
                    {
                        dl.PopClipRect();
                    }
                    return new Vector2(constraint.Width, natural);
                }
                case TextConstraint.FitMode.Wrap:
                {
                    // CSS line boxes: the advance stays FRACTIONAL and
                    // accumulates unrounded so paragraphs cannot drift;
                    // each glyph run is half-leading-centered inside its
                    // line box and only the draw position rounds. Ink may
                    // overflow the box (overflow: visible), but layout
                    // occupies the constraint width.
                    float natural = ImGui.GetTextLineHeight();
                    float advance = constraint.LineHeight is { } multiplier
                        ? size * multiplier * ImGuiHelpers.GlobalScale
                        : natural;
                    float halfLeading = (advance - natural) * 0.5f;
                    float y = origin.Y;
                    foreach (var line in WrapResolved(
                        text, constraint.Width, constraint.Whitespace))
                    {
                        float offset = AlignOffset(
                            constraint.Alignment,
                            constraint.Width,
                            MeasureLine(line));
                        DrawLine(
                            dl,
                            new Vector2(
                                MathF.Round(origin.X + offset),
                                MathF.Round(y + halfLeading)),
                            packed, line);
                        y += advance;
                    }
                    return new Vector2(constraint.Width, MathF.Ceiling(y - origin.Y));
                }
                default:
                    dl.AddText(origin, packed, text);
                    return measure ? ImGui.CalcTextSize(text) : default;
            }
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    private static float AlignOffset(TextAlign alignment, float box, float run) =>
        alignment switch
        {
            TextAlign.Center => (box - run) * 0.5f,
            TextAlign.End => box - run,
            _ => 0f,
        };

    /// <summary>
    /// Unquantized run width from per-glyph advances at the current
    /// font scale. ImGui's CalcTextSize CEILS its result to whole
    /// pixels, so a fit decision made on it accepts anything within the
    /// rounding slack — one extra character at the truncation boundary,
    /// a late line break at a wrap boundary. The browser compares
    /// FRACTIONAL advance sums, so every fit comparison here does too.
    /// </summary>
    private static float FractionalTextWidth(ReadOnlySpan<char> text)
    {
        var font = ImGui.GetFont();
        float scale = ImGui.GetFontSize() / font.FontSize;
        float width = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            // Astral codepoints resolve to the font's single fallback
            // glyph, exactly as AddText renders them.
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1]))
            {
                width += font.GetCharAdvance(font.FallbackChar) * scale;
                i++;
                continue;
            }
            width += font.GetCharAdvance(text[i]) * scale;
        }
        return width;
    }

    /// <summary>Text-element boundary scratch for <see cref="TruncateResolved"/>.
    /// A run is only ever fitted while drawing, which is main-thread only,
    /// and the buffer never escapes the call — so one instance serves every
    /// resolve instead of a fresh list per fit.</summary>
    private static readonly List<int> TruncationBoundaries = [];

    /// <summary>Grapheme-cluster ellipsis backoff in the CURRENTLY PUSHED
    /// face — truncation and rendering always agree on the same font, and
    /// the fit decision uses fractional advances like the browser's. When
    /// even the ellipsis alone cannot fit, the ORIGINAL run is returned —
    /// Blink drops the ellipsis and clips the raw text, and the canonical
    /// renderer's clip rectangle does the same here.</summary>
    private static string TruncateResolved(string text, float width)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (FractionalTextWidth(text) <= width)
            return text;
        float ellipsis = FractionalTextWidth("…");
        if (ellipsis > width)
            return text;

        // Prefix boundaries fall on whole text elements (grapheme
        // clusters): surrogate pairs and combining sequences never split.
        var boundaries = TruncationBoundaries;
        boundaries.Clear();
        for (int index = 0; index < text.Length;)
        {
            boundaries.Add(index);
            index += StringInfo.GetNextTextElementLength(text.AsSpan(index));
        }

        // Advances are non-negative, so prefix width rises monotonically
        // with the boundary and the LAST fitting prefix is a binary search.
        // Walking back from the end re-measured every rejected prefix, which
        // is quadratic in exactly the common case: a run that only just
        // overflows.
        int low = 1;
        int high = boundaries.Count - 1;
        int fit = 0;
        while (low <= high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            if (FractionalTextWidth(text.AsSpan(0, boundaries[middle])) + ellipsis
                <= width)
            {
                fit = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return fit == 0
            ? "…"
            : string.Concat(text.AsSpan(0, boundaries[fit]), "…");
    }

    private const float TabStopSpaces = 8f;

    /// <summary>Measures one laid-out line, expanding preserved tabs to
    /// 8-space-width stops from the line start (CSS <c>tab-size: 8</c>).</summary>
    private static float MeasureLine(string line)
    {
        if (!line.Contains('\t'))
            return FractionalTextWidth(line);
        float tab = FractionalTextWidth(" ") * TabStopSpaces;
        float x = 0f;
        var segments = line.Split('\t');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0)
                x = (MathF.Floor(x / tab) + 1f) * tab;
            x += FractionalTextWidth(segments[i]);
        }
        return x;
    }

    /// <summary>Draws one laid-out line with the same tab expansion
    /// <see cref="MeasureLine"/> measures.</summary>
    private static void DrawLine(
        ImDrawListPtr dl, Vector2 position, uint packed, string line)
    {
        if (!line.Contains('\t'))
        {
            dl.AddText(position, packed, line);
            return;
        }
        float tab = FractionalTextWidth(" ") * TabStopSpaces;
        float x = 0f;
        var segments = line.Split('\t');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0)
                x = (MathF.Floor(x / tab) + 1f) * tab;
            dl.AddText(
                new Vector2(MathF.Round(position.X + x), position.Y),
                packed, segments[i]);
            x += FractionalTextWidth(segments[i]);
        }
    }

    /// <summary>Greedy word wrap in the currently pushed face under the
    /// typed whitespace policy documented at
    /// <see cref="TextConstraint.Wrap"/>.</summary>
    private static IEnumerable<string> WrapResolved(
        string text, float width, TextWhitespace whitespace)
    {
        if (whitespace == TextWhitespace.Normal)
        {
            // CSS normal: newlines and tabs are ordinary collapsible
            // whitespace — ONE paragraph, single-space separated.
            return WrapCollapsed(
                text.Replace('\n', ' ').Replace('\t', ' '), width);
        }
        return WrapParagraphs(text, width, whitespace);
    }

    private static IEnumerable<string> WrapParagraphs(
        string text, float width, TextWhitespace whitespace)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            bool any = false;
            var lines = whitespace == TextWhitespace.PreWrap
                ? WrapPreserved(paragraph, width)
                : WrapCollapsed(paragraph.Replace('\t', ' '), width);
            foreach (var line in lines)
            {
                any = true;
                yield return line;
            }
            if (!any)
                yield return string.Empty; // explicit blank line
        }
    }

    /// <summary>Collapsing wrap (CSS normal / pre-line paragraph): space
    /// runs collapse and vanish at line breaks; the first word of a line
    /// always lands even over-wide (overflow-wrap: normal).</summary>
    private static IEnumerable<string> WrapCollapsed(string paragraph, float width)
    {
        var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string line = string.Empty;
        foreach (var word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (FractionalTextWidth(candidate) <= width || line.Length == 0)
            {
                line = candidate;
                continue;
            }
            yield return line;
            line = word;
        }
        if (line.Length > 0)
            yield return line;
    }

    /// <summary>Preserving wrap (CSS pre-wrap paragraph): spaces and tabs
    /// stay in the text; breaks happen only before words, so break-point
    /// whitespace hangs at the end of the previous line.</summary>
    private static IEnumerable<string> WrapPreserved(string paragraph, float width)
    {
        var builder = new StringBuilder();
        bool hasInk = false;
        int index = 0;
        while (index < paragraph.Length)
        {
            int start = index;
            bool isSpace = paragraph[index] is ' ' or '\t';
            while (index < paragraph.Length
                && (paragraph[index] is ' ' or '\t') == isSpace)
                index++;
            string token = paragraph[start..index];
            if (isSpace)
            {
                builder.Append(token);
                continue;
            }
            string candidate = builder.ToString() + token;
            if (!hasInk || MeasureLine(candidate) <= width)
            {
                builder.Append(token);
                hasInk = true;
                continue;
            }
            yield return builder.ToString();
            builder.Clear();
            builder.Append(token);
        }
        if (builder.Length > 0)
            yield return builder.ToString();
    }
}
