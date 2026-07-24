using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>picto GlassModal size presets (widths 440/560/680).</summary>
public enum ModalSize
{
    Small,
    Medium,
    Large,
}

public static partial class Crystarium
{
    // Footer right-alignment uses the previous frame's measured width (standard
    // ImGui trick — avoids double-rendering children and their ID collisions).
    private static readonly Dictionary<string, float> _modalFooterWidths = new();

    /// <summary>
    /// Glass modal — pixel transcription of picto shared/ui/GlassModal/GlassModal.module.css:
    /// backdrop rgba(0,0,0,.55); glass panel (blur in-game via GlassChrome) with border
    /// trio, radius 8; 44px header (14px/500 title, 24px close button, inset bottom
    /// border); 16px body padding; 44px footer (black@.10, inset top border,
    /// right-aligned children). Real ImGui modal popup — blocks input behind it.
    /// <code>
    ///   if (Crystarium.Button("Open")) modalOpen = true;
    ///   Crystarium.Modal("##import", ref modalOpen, "Import pose",
    ///       body: () => { ... },
    ///       footer: () => { Crystarium.Button("Import", Cls.Primary); });
    /// </code>
    /// </summary>
    /// <returns>True on the frame the modal closes.</returns>
    public static bool Modal(string id, ref bool open, string title, Action body,
        Action? footer = null, ModalSize size = ModalSize.Small, float? height = null,
        Vector2? position = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        string popupId = $"{title}##{id}";

        if (open && !ImGui.IsPopupOpen(popupId))
            ImGui.OpenPopup(popupId);
        if (!open) return false;

        float width = size switch
        {
            ModalSize.Medium => 560f,
            ModalSize.Large => 680f,
            _ => 440f,
        } * scale;
        float barHeight = 44f * scale;
        float totalHeight = (height ?? 280f) * scale;
        float rounding = 8f * scale; // radius-lg

        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(position ?? (displaySize - new Vector2(width, totalHeight)) / 2f, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, totalHeight));

        // Persistent, not pushed: ImGui draws the modal dim outside this call's
        // push/pop bracket. rgba(0,0,0,.55) is the design constant (GlassModal backdrop).
        ImGui.GetStyle().Colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0f, 0f, 0f, 0.55f);

        ImGui.PushStyleColor(ImGuiCol.PopupBg, GlassChrome.BackgroundColor);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f); // border trio drawn manually

        bool closedThisFrame = false;
        bool keepOpen = open;
        if (ImGui.BeginPopupModal(popupId, ref keepOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            var winMin = ImGui.GetWindowPos();
            var winMax = winMin + ImGui.GetWindowSize();

            GlassChrome.PrependBlur(dl, winMin, winMax, rounding);
            Norvrandt.Box(winMin, winMax, new BoxStyle
            {
                BorderWidth = 1f,
                BorderRadius = 8f,
                BorderTopColor = Theme.Glass.BorderTop,
                BorderLeftColor = Theme.Glass.BorderSide,
                BorderRightColor = Theme.Glass.BorderSide,
                BorderBottomColor = Theme.Glass.BorderBottom,
            });

            var theme = Norvrandt.Sheet.CurrentTheme;

            // ── Header: title 14px/500 at 16px, close 24×24 at right 10px,
            //    inset bottom border (border-secondary).
            var titleFont = FontRegistry.Resolve(FontFamily.Default, FontWeight.Medium, 14f);
            bool titlePushed = titleFont is { Available: true };
            if (titlePushed) titleFont!.Push();
            var titleSize = ImGui.CalcTextSize(title);
            dl.AddText(winMin + new Vector2(16f * scale, (barHeight - titleSize.Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text)), title);
            if (titlePushed) titleFont!.Pop();

            float closeSize = 24f * scale;
            ImGui.SetCursorScreenPos(new Vector2(winMax.X - 10f * scale - closeSize, winMin.Y + (barHeight - closeSize) * 0.5f));
            var closeHit = Interactive.Reserve($"{id}##close", new Vector2(closeSize, closeSize), disabled: false);
            if (closeHit.Hovered)
                dl.AddRectFilled(closeHit.ScreenMin, closeHit.ScreenMax,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))), 5f * scale);
            {
                // Tabler X ("M18 6l-12 12" + "M6 6l12 12") at 14px, .7 → 1 on hover
                float iconSpan = 14f * scale;
                float unit = iconSpan / 24f;
                var o = closeHit.ScreenMin + new Vector2((closeSize - iconSpan) * 0.5f, (closeSize - iconSpan) * 0.5f);
                var xCol = ColorEx.ApplyAlpha(theme.Text with { W = closeHit.Hovered ? 1f : 0.7f });
                uint xU32 = ImGui.ColorConvertFloat4ToU32(xCol);
                dl.PathLineTo(o + new Vector2(18f, 6f) * unit);
                dl.PathLineTo(o + new Vector2(6f, 18f) * unit);
                dl.PathStroke(xU32, ImDrawFlags.None, 2f * unit);
                dl.PathLineTo(o + new Vector2(6f, 6f) * unit);
                dl.PathLineTo(o + new Vector2(18f, 18f) * unit);
                dl.PathStroke(xU32, ImDrawFlags.None, 2f * unit);
            }
            if (closeHit.Clicked) keepOpen = false;

            dl.AddRectFilled(
                new Vector2(winMin.X, winMin.Y + barHeight - 1f * scale),
                new Vector2(winMax.X, winMin.Y + barHeight),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))));

            // ── Footer chrome (drawn before body so its strip sits under nothing)
            float footerTop = winMax.Y - barHeight;
            if (footer != null)
            {
                dl.AddRectFilled(new Vector2(winMin.X, footerTop), winMax,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(0f, 0f, 0f, 0.10f))),
                    rounding, ImDrawFlags.RoundCornersBottom);
                dl.AddRectFilled(
                    new Vector2(winMin.X, footerTop),
                    new Vector2(winMax.X, footerTop + 1f * scale),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))));
            }

            // ── Body: padding 16, scrollable between the bars.
            float bodyHeight = totalHeight - barHeight - (footer != null ? barHeight : 0f);
            ImGui.SetCursorScreenPos(winMin + new Vector2(0f, barHeight));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f * scale, 16f * scale));
            // AlwaysUseWindowPadding: borderless children ignore WindowPadding otherwise.
            if (ImGui.BeginChild($"{id}##body", new Vector2(width, bodyHeight), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                body();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();

            // ── Footer content: right-aligned via last frame's measured width.
            if (footer != null)
            {
                _modalFooterWidths.TryGetValue(popupId, out float lastWidth);
                float x = winMin.X + MathF.Max(12f * scale, width - 12f * scale - lastWidth);
                ImGui.SetCursorScreenPos(new Vector2(x, footerTop + (barHeight - 32f * scale) * 0.5f));
                ImGui.BeginGroup();
                footer();
                ImGui.EndGroup();
                _modalFooterWidths[popupId] = ImGui.GetItemRectSize().X;
            }

            if (!keepOpen)
            {
                ImGui.CloseCurrentPopup();
                open = false;
                closedThisFrame = true;
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(1);
        return closedThisFrame;
    }
}
