using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Plain inline text. Cascade-inherited color/font/opacity unless overridden.</summary>
    public static void Text(string text)
        => TextCore(text, default, null);
    public static void Text(string text, StyleClass cls)
        => TextCore(text, cls, null);
    public static void Text(string text, StyleClassSet classes)
        => TextCore(text, classes, null);
    public static void Text(string text, in TextProps props)
        => TextCore(text, props.Classes, props.Style);

    private static void TextCore(string text, StyleClassSet classes, TextStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var resolved = Stylesheet.ResolveText(classes, PseudoState.None);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        if (resolved.Display == UI.Display.None) return;

        float scale = PoserUI.Scale;

        // Margin top
        if (resolved.Margin.HasValue && resolved.Margin.Value.Top > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + resolved.Margin.Value.Top * scale);

        // Vertical centering when inside a sized cell
        float ambientH = AvailableHeight;
        if (ambientH > 0f)
        {
            float offsetY = (ambientH - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        }

        // Resolve max width for text-overflow / wrap.
        float maxPx;
        if (resolved.MaxWidth.HasValue && resolved.MaxWidth.Value.Mode == SizingMode.Fixed)
            maxPx = resolved.MaxWidth.Value.Value * scale;
        else if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            maxPx = resolved.Width.Value.Value * scale;
        else
            maxPx = AvailableWidth;

        // Apply text-overflow truncation.
        string display = text;
        var overflow = resolved.TextOverflow ?? UI.TextOverflow.Visible;
        if (overflow == UI.TextOverflow.Ellipsis)
        {
            display = TruncateWithEllipsis(text, maxPx);
        }

        // Color cascade — push once if specified
        bool pushed = false;
        if (resolved.Color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, resolved.Color.Value);
            pushed = true;
        }

        var pos = ImGui.GetCursorScreenPos();
        var textColor = resolved.Color ?? UIColors.Text;
        var textColorU32 = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(textColor));

        // Text shadow (drawn before main text)
        if (resolved.TextShadow.HasValue)
        {
            DrawTextShadow(pos, display, resolved.TextShadow.Value, scale);
        }

        // White-space handling
        var ws = resolved.WhiteSpace ?? UI.WhiteSpace.Normal;
        var drawList = ImGui.GetWindowDrawList();
        if (overflow == UI.TextOverflow.Clip || overflow == UI.TextOverflow.Ellipsis)
        {
            // Clip to max width so visible-overflow doesn't escape.
            drawList.PushClipRect(pos, pos + new System.Numerics.Vector2(maxPx, ImGui.GetTextLineHeight() * 2f), true);
            ImGui.Text(display);
            drawList.PopClipRect();
            // Advance the cursor as if the full width was reserved.
            ImGui.SameLine(0, 0);
            ImGui.Dummy(new System.Numerics.Vector2(0, 0));
        }
        else if (ws == UI.WhiteSpace.Nowrap)
        {
            ImGui.Text(display);
        }
        else
        {
            // Normal wrap at max width.
            ImGui.PushTextWrapPos(pos.X + maxPx);
            ImGui.Text(display);
            ImGui.PopTextWrapPos();
        }

        if (pushed) ImGui.PopStyleColor();

        if (resolved.Margin.HasValue && resolved.Margin.Value.Bottom > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + resolved.Margin.Value.Bottom * scale);
    }

    private static void DrawTextShadow(System.Numerics.Vector2 textPos, string text, TextShadow shadow, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var offset = new System.Numerics.Vector2(shadow.OffsetX, shadow.OffsetY) * scale;
        if (shadow.Blur > 0f)
        {
            // Cheap glow: draw the text multiple times in 8 directions at decreasing alpha.
            int steps = System.Math.Max(2, (int)shadow.Blur);
            for (int s = steps; s >= 1; s--)
            {
                float t = s / (float)steps;
                float r = shadow.Blur * scale * t;
                var col = shadow.Color;
                col.W *= (1f - t);
                uint c = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(col));
                for (int a = 0; a < 8; a++)
                {
                    float ang = a * (System.MathF.PI / 4f);
                    var d = new System.Numerics.Vector2(System.MathF.Cos(ang), System.MathF.Sin(ang)) * r;
                    drawList.AddText(textPos + offset + d, c, text);
                }
            }
        }
        else
        {
            uint c = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(shadow.Color));
            drawList.AddText(textPos + offset, c, text);
        }
    }

    private static string TruncateWithEllipsis(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var size = ImGui.CalcTextSize(text);
        if (size.X <= maxWidth) return text;
        var ellipsis = ImGui.CalcTextSize("…");
        float avail = maxWidth - ellipsis.X;
        if (avail <= 0) return "…";
        int left = 0, right = text.Length;
        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            var sub = ImGui.CalcTextSize(text[..mid]);
            if (sub.X <= avail) left = mid; else right = mid - 1;
        }
        return left == 0 ? "…" : text[..left] + "…";
    }

    /// <summary>Thin separator line at 50% border opacity.</summary>
    public static void Separator() => Controls.PoserUI.Separator();

    /// <summary>Image element. Renders a Dalamud texture wrap at the given size.</summary>
    public static void Image(IDalamudTextureWrap texture, Vector2 size, Vector4? tint = null)
    {
        if (texture == null) return;
        if (tint.HasValue)
            ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, tint.Value);
        else
            ImGui.Image(texture.Handle, size);
    }
}
