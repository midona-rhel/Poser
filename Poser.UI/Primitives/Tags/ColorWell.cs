using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Color well — picto M5 <c>.well</c>: 28×28, radius 6, 1px
    /// <c>--color-border-primary</c> border, filled with the current color.
    /// Clicking opens an ImGui color picker in a glass popup (documented
    /// deviation: the picker interior is ImGui's, only the chrome is picto).
    /// Returns true while the color is being edited.
    /// </summary>
    public static bool ColorWell(string id, ref Vector4 color)
    {
        Stylesheet.EnsureInitialized();
        float scale = ImGuiHelpers.GlobalScale;

        var size = new Vector2(28f, 28f) * scale;
        var hit = Interactive.Reserve(id, size, disabled: false, Norvrandt.AvailableHeight);

        var dl = ImGui.GetWindowDrawList();
        float r = 6f * scale;
        dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color with { W = 1f })), r);
        // 1px border painted inside the box edge (CSS border-box)
        dl.AddRect(hit.ScreenMin + new Vector2(0.5f, 0.5f), hit.ScreenMax - new Vector2(0.5f, 0.5f),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.14f))), r, ImDrawFlags.None, 1f * scale);

        string popupId = id + "_picker";
        if (hit.Clicked) ImGui.OpenPopup(popupId);

        // Glass popup chrome, same recipe as ContextMenu (blur + border trio).
        bool changed = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, GlassChrome.BackgroundColor);
        if (ImGui.BeginPopup(popupId))
        {
            var pdl = ImGui.GetWindowDrawList();
            var winMin = ImGui.GetWindowPos();
            var winMax = winMin + ImGui.GetWindowSize();
            GlassChrome.PrependBlur(pdl, winMin, winMax, 8f * scale);
            Norvrandt.Box(winMin, winMax, new BoxStyle
            {
                BorderWidth = 1f,
                BorderRadius = 8f,
                BorderTopColor = Theme.Glass.BorderTop,
                BorderLeftColor = Theme.Glass.BorderSide,
                BorderRightColor = Theme.Glass.BorderSide,
                BorderBottomColor = Theme.Glass.BorderBottom,
            });
            ImGui.SetNextItemWidth(200f * scale);
            changed = ImGui.ColorPicker4(id + "_pk", ref color,
                ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview);
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        return changed;
    }

    /// <summary>
    /// Accent swatch — picto M5 <c>.swatch</c>: 28px circle, inset 1px ring
    /// white @ .18; active state adds a 2px <c>--color-bg-app</c> gap ring plus a
    /// 2px <c>--color-primary</c> outer ring. Returns true when clicked.
    /// </summary>
    public static bool Swatch(string id, Vector4 color, bool active)
    {
        Stylesheet.EnsureInitialized();
        float scale = ImGuiHelpers.GlobalScale;

        var size = new Vector2(28f, 28f) * scale;
        var hit = Interactive.Reserve(id, size, disabled: false, Norvrandt.AvailableHeight);

        var dl = ImGui.GetWindowDrawList();
        var center = (hit.ScreenMin + hit.ScreenMax) * 0.5f;
        float radius = 14f * scale;

        // box-shadow spread rings drawn as filled discs back-to-front — one clean
        // AA boundary per edge (stroked circles fringe on both stroke edges).
        if (active)
        {
            dl.AddCircleFilled(center, radius + 4f * scale,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(50 / 255f, 151 / 255f, 255 / 255f, 1f))), 64);
            dl.AddCircleFilled(center, radius + 2f * scale,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(24 / 255f, 25 / 255f, 27 / 255f, 1f))), 64);
        }
        dl.AddCircleFilled(center, radius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color with { W = 1f })), 64);
        // inset ring: box-shadow inset 0 0 0 1px rgba(255,255,255,.18) — 13..14px band
        dl.AddCircle(center, radius - 0.5f * scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.18f))), 64, 1f * scale);

        return hit.Clicked;
    }
}
