using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>How a text run resolves against an available width.</summary>
public enum TextFit
{
    /// <summary>Natural content width; never cut.</summary>
    Intrinsic,
    /// <summary>One line, ellipsis-truncated inside the width
    /// (Picto's <c>overflow:hidden; text-overflow:ellipsis;
    /// white-space:nowrap</c> idiom).</summary>
    Truncate,
    /// <summary>Word-wrapped inside the width at the style's line
    /// height.</summary>
    Wrap,
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
    /// <summary>CSS-pixel font size; null resolves Typography.BodySize.</summary>
    public float? Size { get; init; }

    /// <summary>null resolves Regular.</summary>
    public FontWeight? Weight { get; init; }

    public FontFamily Family { get; init; }

    /// <summary>null resolves the theme's primary text color.</summary>
    public Vector4? Color { get; init; }

    /// <summary>Applies the disabled opacity to the resolved color.</summary>
    public bool Disabled { get; init; }

    /// <summary>Wrap line height as a multiplier of the font size (CSS
    /// line-height); null uses the font's natural line box.</summary>
    public float? LineHeight { get; init; }
}

public static partial class Crystarium
{
    /// <summary>Plain inline body text from the active theme.</summary>
    public static void Text(string text)
        => Text(text, default);

    /// <summary>Inline (cursor-flow) text at its intrinsic width.</summary>
    public static void Text(string text, in TextStyle style)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = DrawTextRun(origin, text, style, float.MaxValue, TextFit.Intrinsic);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>Inline (cursor-flow) text fitted to a pixel width.</summary>
    public static void Text(string text, in TextStyle style, float width, TextFit fit)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = DrawTextRun(origin, text, style, width, fit);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>Screen-positioned text for composed chrome and canvases.
    /// Draws into the current window's draw list and submits NO layout
    /// item.</summary>
    public static void TextAt(Vector2 position, string text, in TextStyle style)
        => DrawTextRun(position, text, style, float.MaxValue, TextFit.Intrinsic);

    /// <summary>Screen-positioned text fitted to a pixel width.</summary>
    public static void TextAt(
        Vector2 position, string text, in TextStyle style, float width, TextFit fit)
        => DrawTextRun(position, text, style, width, fit);

    /// <summary>Measures the run at its intrinsic size.</summary>
    public static Vector2 MeasureText(string text, in TextStyle style)
    {
        var (font, pushed, _, _) = ResolveStyle(style);
        var measured = ImGui.CalcTextSize(text);
        if (pushed)
            font!.Pop();
        return measured;
    }

    /// <summary>Truncates to the width with an ellipsis, measured at the
    /// style's ACTUAL face and weight.</summary>
    public static string TruncateText(string text, in TextStyle style, float width)
    {
        var (font, pushed, _, _) = ResolveStyle(style);
        string fitted = TruncateResolved(text, width);
        if (pushed)
            font!.Pop();
        return fitted;
    }

    private static (IFontHandle? Font, bool Pushed, float Size, Vector4 Color)
        ResolveStyle(in TextStyle style)
    {
        float size = style.Size ?? ActiveTheme.Typography.BodySize;
        var weight = style.Weight ?? FontWeight.Regular;
        var color = style.Color ?? ActiveTheme.Text;
        if (style.Disabled)
            color.W *= ActiveTheme.Chrome.DisabledOpacity;
        var font = FontRegistry.Resolve(
            style.Family == FontFamily.Default ? FontFamily.Default : style.Family,
            weight, size);
        bool pushed = font is { Available: true };
        if (pushed)
            font!.Push();
        return (font, pushed, size, color);
    }

    /// <summary>The one text renderer: resolves the style, fits the run,
    /// snaps the origin to whole pixels, and draws through the window
    /// draw list. Returns the drawn size.</summary>
    private static Vector2 DrawTextRun(
        Vector2 position, string text, in TextStyle style, float width, TextFit fit)
    {
        var (font, pushed, size, color) = ResolveStyle(style);
        try
        {
            var dl = ImGui.GetWindowDrawList();
            uint packed = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
            var origin = ActiveTheme.Optical.Snap(position);
            Vector2 drawn;
            switch (fit)
            {
                case TextFit.Truncate:
                {
                    string fitted = TruncateResolved(text, width);
                    dl.AddText(origin, packed, fitted);
                    drawn = ImGui.CalcTextSize(fitted);
                    break;
                }
                case TextFit.Wrap:
                {
                    float advance = style.LineHeight is { } lineHeight
                        ? MathF.Round(size * lineHeight * ImGuiHelpers.GlobalScale)
                        : ImGui.GetTextLineHeight();
                    float y = origin.Y;
                    float maxWidth = 0f;
                    foreach (var line in WrapResolved(text, width))
                    {
                        dl.AddText(new Vector2(origin.X, MathF.Round(y)), packed, line);
                        maxWidth = MathF.Max(maxWidth, ImGui.CalcTextSize(line).X);
                        y += advance;
                    }
                    drawn = new Vector2(maxWidth, y - origin.Y);
                    break;
                }
                default:
                    dl.AddText(origin, packed, text);
                    drawn = ImGui.CalcTextSize(text);
                    break;
            }
            return drawn;
        }
        finally
        {
            if (pushed)
                font!.Pop();
        }
    }

    /// <summary>Ellipsis backoff measured in the CURRENTLY PUSHED face —
    /// truncation and rendering always agree on the same font.</summary>
    private static string TruncateResolved(string text, float width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0f)
            return string.Empty;
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        for (int keep = text.Length - 1; keep > 0; keep--)
        {
            string candidate = text[..keep] + "…";
            if (ImGui.CalcTextSize(candidate).X <= width)
                return candidate;
        }
        return "…";
    }

    /// <summary>Greedy word wrap in the currently pushed face. Preserves
    /// explicit newlines; a single word wider than the width hard-breaks.</summary>
    private static IEnumerable<string> WrapResolved(string text, float width)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                yield return string.Empty;
                continue;
            }
            var words = paragraph.Split(' ');
            string line = string.Empty;
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (ImGui.CalcTextSize(candidate).X <= width || line.Length == 0)
                {
                    line = candidate;
                    continue;
                }
                yield return line;
                line = word;
            }
            // A single over-wide word: emit as-is rather than losing it.
            if (line.Length > 0)
                yield return line;
        }
    }
}
