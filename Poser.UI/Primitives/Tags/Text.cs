using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// A typed width constraint for a text run. Intrinsic text carries no
/// width; truncation REQUIRES one; wrapping requires one and owns its
/// optional CSS line-height. Invalid combinations are unrepresentable
/// and non-positive dimensions are rejected at construction.
/// </summary>
public readonly struct TextConstraint
{
    internal enum FitMode { Intrinsic, Truncate, Wrap }

    internal FitMode Mode { get; }
    internal float Width { get; }
    internal float? LineHeight { get; }

    private TextConstraint(FitMode mode, float width, float? lineHeight)
    {
        Mode = mode;
        Width = width;
        LineHeight = lineHeight;
    }

    /// <summary>Natural content width; never cut.</summary>
    public static TextConstraint Intrinsic => default;

    /// <summary>One line, ellipsis-truncated inside the pixel width
    /// (Picto's <c>overflow:hidden; text-overflow:ellipsis;
    /// white-space:nowrap</c> idiom).</summary>
    public static TextConstraint Truncate(float width)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Truncation requires a positive pixel width.");
        return new TextConstraint(FitMode.Truncate, width, null);
    }

    /// <summary>
    /// Word wrap inside the pixel width, on the CSS-compatible contract:
    /// explicit newlines always break; runs of ordinary spaces collapse
    /// and disappear at line breaks (CSS <c>white-space: normal</c>); a
    /// single over-wide word OVERFLOWS its line (CSS
    /// <c>overflow-wrap: normal</c>) rather than being hard-broken; the
    /// line advance is the FRACTIONAL CSS line height, accumulated
    /// unrounded so long paragraphs cannot drift; each line's glyph run
    /// sits half-leading-centered inside its explicit line box. A null
    /// line height uses the font's natural line box.
    /// </summary>
    public static TextConstraint Wrap(float width, float? lineHeight = null)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Wrapping requires a positive pixel width.");
        if (lineHeight is { } multiplier && !(multiplier > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(lineHeight), multiplier, "A line height must be positive.");
        return new TextConstraint(FitMode.Wrap, width, lineHeight);
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

    /// <summary>Inline (cursor-flow) text under a typed constraint.</summary>
    public static void Text(string text, in TextStyle style, TextConstraint constraint)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = DrawTextRun(origin, text, style, constraint);
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

    /// <summary>Measures the run at its intrinsic size.</summary>
    public static Vector2 MeasureText(string text, in TextStyle style)
    {
        var (font, pushed, _, _) = ResolveStyle(style);
        try
        {
            return ImGui.CalcTextSize(text);
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    /// <summary>
    /// Truncates to the pixel width with an ellipsis, measured at the
    /// style's ACTUAL face and weight, backing off whole grapheme
    /// clusters — surrogate pairs and combining sequences are never
    /// split. The returned text is GUARANTEED to fit the width; when
    /// even the ellipsis alone cannot fit, the result is empty.
    /// </summary>
    public static string TruncateText(string text, in TextStyle style, float width)
    {
        if (!(width > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "Truncation requires a positive pixel width.");
        var (font, pushed, _, _) = ResolveStyle(style);
        try
        {
            return TruncateResolved(text, width);
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    private static (IFontHandle? Font, bool Pushed, float Size, Vector4 Color)
        ResolveStyle(in TextStyle style)
    {
        if (style.Size is { } requested && !(requested > 0f))
            throw new ArgumentOutOfRangeException(
                nameof(style), requested, "A font size must be positive.");
        float size = style.Size ?? ActiveTheme.Typography.BodySize;
        var weight = style.Weight ?? FontWeight.Regular;
        var color = style.Color ?? ActiveTheme.Text;
        if (style.Disabled)
            color.W *= ActiveTheme.Chrome.DisabledOpacity;
        var font = FontRegistry.Resolve(style.Family, weight, size);
        bool pushed = font is { Available: true };
        if (pushed)
            font!.Push();
        return (font, pushed, size, color);
    }

    /// <summary>The one text renderer: resolves the style, fits the run,
    /// snaps the origin to whole pixels, and draws through the window
    /// draw list. Returns the drawn size.</summary>
    private static Vector2 DrawTextRun(
        Vector2 position, string text, in TextStyle style, TextConstraint constraint)
    {
        var (font, pushed, size, color) = ResolveStyle(style);
        try
        {
            var dl = ImGui.GetWindowDrawList();
            uint packed = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
            var origin = ActiveTheme.Optical.Snap(position);
            switch (constraint.Mode)
            {
                case TextConstraint.FitMode.Truncate:
                {
                    string fitted = TruncateResolved(text, constraint.Width);
                    dl.AddText(origin, packed, fitted);
                    return ImGui.CalcTextSize(fitted);
                }
                case TextConstraint.FitMode.Wrap:
                {
                    // CSS line boxes: the advance stays FRACTIONAL and
                    // accumulates unrounded so paragraphs cannot drift;
                    // each glyph run is half-leading-centered inside its
                    // line box and only the draw position rounds.
                    float natural = ImGui.GetTextLineHeight();
                    float advance = constraint.LineHeight is { } multiplier
                        ? size * multiplier * ImGuiHelpers.GlobalScale
                        : natural;
                    float halfLeading = (advance - natural) * 0.5f;
                    float y = origin.Y;
                    float maxWidth = 0f;
                    foreach (var line in WrapResolved(text, constraint.Width))
                    {
                        dl.AddText(
                            new Vector2(origin.X, MathF.Round(y + halfLeading)),
                            packed, line);
                        maxWidth = MathF.Max(maxWidth, ImGui.CalcTextSize(line).X);
                        y += advance;
                    }
                    return new Vector2(maxWidth, MathF.Ceiling(y - origin.Y));
                }
                default:
                    dl.AddText(origin, packed, text);
                    return ImGui.CalcTextSize(text);
            }
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    /// <summary>Grapheme-cluster ellipsis backoff in the CURRENTLY PUSHED
    /// face — truncation and rendering always agree on the same font, and
    /// every candidate is measured before it is returned.</summary>
    private static string TruncateResolved(string text, float width)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        if (ImGui.CalcTextSize("…").X > width)
            return string.Empty;

        // Prefix boundaries fall on whole text elements (grapheme
        // clusters): surrogate pairs and combining sequences never split.
        var boundaries = new List<int>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            boundaries.Add(elements.ElementIndex);
        for (int element = boundaries.Count - 1; element >= 1; element--)
        {
            string candidate = text[..boundaries[element]] + "…";
            if (ImGui.CalcTextSize(candidate).X <= width)
                return candidate;
        }
        return "…";
    }

    /// <summary>Greedy word wrap in the currently pushed face, on the
    /// contract documented at <see cref="TextConstraint.Wrap"/>.</summary>
    private static IEnumerable<string> WrapResolved(string text, float width)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            // Runs of ordinary spaces collapse and leading/trailing
            // spaces vanish (CSS white-space: normal); an empty
            // paragraph is an explicit blank line.
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                yield return string.Empty;
                continue;
            }
            string line = string.Empty;
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (ImGui.CalcTextSize(candidate).X <= width || line.Length == 0)
                {
                    // The first word of a line always lands even when
                    // over-wide: CSS overflow-wrap normal lets it
                    // overflow rather than hard-breaking it.
                    line = candidate;
                    continue;
                }
                yield return line;
                line = word;
            }
            if (line.Length > 0)
                yield return line;
        }
    }
}
