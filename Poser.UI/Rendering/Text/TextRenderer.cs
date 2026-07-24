using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// Single entrypoint for rendering text with full <see cref="ElementStyle"/> coverage:
/// <see cref="TextAlign"/>, <see cref="TextOverflow"/>, <see cref="WhiteSpace"/>,
/// <see cref="ElementStyle.LineHeight"/>, <see cref="ElementStyle.LetterSpacing"/>,
/// <see cref="ElementStyle.TextShadow"/>, plus the cascade-pushed font (which already
/// honors <see cref="ElementStyle.FontFamily"/> and <see cref="ElementStyle.FontSize"/>).
/// </summary>
public static class TextRenderer
{
    /// <summary>
    /// Render <paramref name="text"/> inside the rect <paramref name="boxMin"/>..<paramref name="boxMax"/>
    /// according to <paramref name="style"/>. <paramref name="defaultColor"/> is used when
    /// <see cref="ElementStyle.Color"/> is unset.
    /// </summary>
    public static void Draw(ImDrawListPtr drawList, Vector2 boxMin, Vector2 boxMax, string text,
        in ElementStyle style, uint defaultColor)
    {
        if (string.IsNullOrEmpty(text)) return;

        var font = ImGui.GetFont();
        float fontSize = ImGui.GetFontSize();
        float scale = ImGuiHelpers.GlobalScale;

        float lineHeightMul = style.LineHeight ?? 1.0f;
        float letterSpacing = (style.LetterSpacing ?? 0f) * scale;
        float lineStep = fontSize * lineHeightMul;

        var color = style.Color.HasValue ? ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(style.Color.Value)) : defaultColor;
        var align = style.TextAlign ?? TextAlign.Start;
        var overflow = style.TextOverflow ?? TextOverflow.Visible;
        var ws = style.WhiteSpace ?? WhiteSpace.Normal;

        float boxWidth = boxMax.X - boxMin.X;
        float boxHeight = boxMax.Y - boxMin.Y;

        // Wrap into lines.
        var lines = (ws == WhiteSpace.Nowrap)
            ? new List<string> { text }
            : WrapLines(text, font, fontSize, letterSpacing, boxWidth, ws == WhiteSpace.Pre);

        // Apply ellipsis to lines that overflow horizontally.
        if (overflow == TextOverflow.Ellipsis)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (MeasureLineWidth(lines[i], font, fontSize, letterSpacing) > boxWidth)
                    lines[i] = TruncateWithEllipsis(lines[i], font, fontSize, letterSpacing, boxWidth);
            }
        }

        // Vertical baseline — top of the first line. Multi-line stacks downward; no vertical alignment in v1.
        float y = boxMin.Y;

        // Hard clip if requested.
        bool clipPushed = false;
        if (overflow == TextOverflow.Clip || overflow == TextOverflow.Ellipsis || ws == WhiteSpace.Nowrap)
        {
            drawList.PushClipRect(boxMin, boxMax, true);
            clipPushed = true;
        }

        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                float lineWidth = MeasureLineWidth(line, font, fontSize, letterSpacing);
                float xStart = align switch
                {
                    TextAlign.Center => boxMin.X + (boxWidth - lineWidth) * 0.5f,
                    TextAlign.End    => boxMax.X - lineWidth,
                    _                => boxMin.X,
                };

                DrawShadows(drawList, font, fontSize, line, new Vector2(xStart, y), letterSpacing, in style);
                DrawLine(drawList, font, fontSize, line, new Vector2(xStart, y), letterSpacing, color);

                y += lineStep;
                if (y > boxMax.Y) break;
            }
        }
        finally
        {
            if (clipPushed) drawList.PopClipRect();
        }
    }

    /// <summary>Measure full text size (post-wrap) for content sizing.</summary>
    public static Vector2 Measure(string text, in ElementStyle style, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;

        var font = ImGui.GetFont();
        float fontSize = ImGui.GetFontSize();
        float scale = ImGuiHelpers.GlobalScale;
        float letterSpacing = (style.LetterSpacing ?? 0f) * scale;
        float lineHeightMul = style.LineHeight ?? 1.0f;
        var ws = style.WhiteSpace ?? WhiteSpace.Normal;

        var lines = (ws == WhiteSpace.Nowrap)
            ? new List<string> { text }
            : WrapLines(text, font, fontSize, letterSpacing, maxWidth, ws == WhiteSpace.Pre);

        float widest = 0f;
        foreach (var line in lines)
        {
            float w = MeasureLineWidth(line, font, fontSize, letterSpacing);
            if (w > widest) widest = w;
        }
        return new Vector2(widest, fontSize * lineHeightMul * lines.Count);
    }

    // ---------- internal helpers ----------

    private static List<string> WrapLines(string text, ImFontPtr font, float fontSize, float letterSpacing,
        float maxWidth, bool preserveNewlines)
    {
        var output = new List<string>();
        // Step 1: split on hard newlines.
        var hardLines = preserveNewlines
            ? text.Split('\n')
            : text.Replace('\n', ' ').Split('\n');

        foreach (var hardLine in hardLines)
        {
            if (maxWidth <= 0f || float.IsInfinity(maxWidth))
            {
                output.Add(hardLine);
                continue;
            }

            var current = string.Empty;
            float currentWidth = 0f;
            int wordStart = 0;
            for (int i = 0; i <= hardLine.Length; i++)
            {
                bool atBoundary = i == hardLine.Length || hardLine[i] == ' ';
                if (!atBoundary) continue;

                string word = hardLine.Substring(wordStart, i - wordStart);
                float wordWidth = MeasureLineWidth(word, font, fontSize, letterSpacing);
                bool needsSpace = current.Length > 0;
                float spaceWidth = needsSpace ? font.GetCharAdvance(' ') + letterSpacing : 0f;

                if (currentWidth + spaceWidth + wordWidth > maxWidth && current.Length > 0)
                {
                    output.Add(current);
                    current = word;
                    currentWidth = wordWidth;
                }
                else
                {
                    current = needsSpace ? current + " " + word : word;
                    currentWidth += spaceWidth + wordWidth;
                }
                wordStart = i + 1;
            }
            output.Add(current);
        }
        return output;
    }

    private static float MeasureLineWidth(string line, ImFontPtr font, float fontSize, float letterSpacing)
    {
        if (string.IsNullOrEmpty(line)) return 0f;
        if (letterSpacing == 0f)
        {
            return ImGui.CalcTextSize(line).X;
        }
        // Manual measure with letter-spacing.
        float w = 0f;
        for (int i = 0; i < line.Length; i++)
            w += font.GetCharAdvance(line[i]) + (i < line.Length - 1 ? letterSpacing : 0f);
        return w;
    }

    private static string TruncateWithEllipsis(string line, ImFontPtr font, float fontSize, float letterSpacing, float maxWidth)
    {
        const string ellipsis = "…";
        float ellipsisWidth = font.GetCharAdvance(ellipsis[0]);
        if (ellipsisWidth >= maxWidth) return ellipsis;

        // Binary-search the longest prefix that fits.
        int lo = 0, hi = line.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string candidate = line.Substring(0, mid);
            float w = MeasureLineWidth(candidate, font, fontSize, letterSpacing) + ellipsisWidth;
            if (w <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return line.Substring(0, lo).TrimEnd() + ellipsis;
    }

    private static void DrawLine(ImDrawListPtr drawList, ImFontPtr font, float fontSize, string text,
        Vector2 pos, float letterSpacing, uint color)
    {
        if (letterSpacing == 0f)
        {
            drawList.AddText(pos, color, text);
            return;
        }
        // Per-char draw with manual advance.
        var cursor = pos;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            drawList.AddText(font, fontSize, cursor, color, c.ToString());
            cursor.X += font.GetCharAdvance(c) + letterSpacing;
        }
    }

    private static void DrawShadows(ImDrawListPtr drawList, ImFontPtr font, float fontSize, string text,
        Vector2 pos, float letterSpacing, in ElementStyle style)
    {
        if (!style.TextShadow.HasValue) return;
        var sh = style.TextShadow.Value;
        var color = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(sh.Color));
        float scale = ImGuiHelpers.GlobalScale;
        var basePos = pos + new Vector2(sh.OffsetX * scale, sh.OffsetY * scale);

        if (sh.Blur <= 0f)
        {
            DrawLine(drawList, font, fontSize, text, basePos, letterSpacing, color);
            return;
        }

        // Cheap blur: stamp the shadow at 4 sub-offsets around base.
        float blur = sh.Blur * scale;
        var offsets = new[] {
            new Vector2( blur,  0f),
            new Vector2(-blur,  0f),
            new Vector2(  0f,  blur),
            new Vector2(  0f, -blur),
        };
        // Quartered alpha so the stamp doesn't over-saturate.
        var stampColor = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(sh.Color with { W = sh.Color.W * 0.25f }));
        foreach (var o in offsets)
            DrawLine(drawList, font, fontSize, text, basePos + o, letterSpacing, stampColor);
        DrawLine(drawList, font, fontSize, text, basePos, letterSpacing, color);
    }
}
