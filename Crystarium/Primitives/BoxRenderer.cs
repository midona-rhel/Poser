using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

/// <summary>
/// Internal helper. Renders a BoxStyle's chrome (shadow → fill → gradient → border)
/// at an explicit screen-space rectangle. No cursor manipulation.
/// </summary>
internal static class BoxRenderer
{
    public static void Draw(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxStyle style)
    {
        float scale = PoserUI.Scale;
        float radius = style.BorderRadius * scale;

        if (style.BoxShadow.HasValue)
        {
            var sh = style.BoxShadow.Value;
            if (sh.Blur > 0f)
            {
                // Soft shadow — falls back to existing DrawControlShadow look for parity.
                DrawHelpers.DrawControlShadow(drawList, min, max, style.BorderRadius, sh.Color.W / 0.20f);
            }
            else
            {
                // Hard offset shadow.
                var offset = new Vector2(sh.OffsetX, sh.OffsetY) * scale;
                drawList.AddRectFilled(min + offset, max + offset,
                    ImGui.ColorConvertFloat4ToU32(sh.Color), radius);
            }
        }

        if (style.BackgroundColor.HasValue)
        {
            var bg = UIColors.ApplyAlpha(style.BackgroundColor.Value);
            drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), radius);
        }

        if (style.RaisedGradient)
        {
            float height = max.Y - min.Y;
            DrawHelpers.DrawButtonGradients(drawList, min, max, height, style.BorderRadius);
        }

        if (style.BorderWidth > 0f && style.BorderColor.HasValue)
        {
            var borderU32 = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(style.BorderColor.Value));
            drawList.AddRect(min, max, borderU32, radius, ImDrawFlags.None, style.BorderWidth * scale);
        }
    }
}
