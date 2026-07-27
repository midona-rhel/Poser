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
            ModalSize.Medium => Theme.Metrics.Floating.MediumWidth,
            ModalSize.Large => Theme.Metrics.Floating.LargeWidth,
            _ => Theme.Metrics.Floating.SmallWidth,
        } * scale;
        float barHeight = Theme.Metrics.Floating.ModalBar * scale;
        float totalHeight = (height
            ?? Theme.Metrics.Floating.DefaultModalHeight) * scale;
        float rounding = Theme.Metrics.Radius.Surface * scale;

        ImGui.SetNextWindowPos(
            position ?? FloatingSurface.PlaceCentered(
                new Vector2(width, totalHeight)),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, totalHeight));

        // Persistent, not pushed: ImGui draws the modal dim outside this call's
        // push/pop bracket. rgba(0,0,0,.55) is the design constant (GlassModal backdrop).
        ImGui.GetStyle().Colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0f, 0f, 0f, 0.55f);

        ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);
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

            FloatingSurface.DrawChrome(
                dl,
                winMin,
                winMax,
                Theme.Metrics.Radius.Surface);

            var theme = Norvrandt.Sheet.CurrentTheme;

            // ── Header: title 14px/500 at 16px, close 24×24 at right 10px,
            //    inset bottom border (border-secondary).
            var titleFont = FontRegistry.Resolve(
                FontFamily.Default,
                FontWeight.Medium,
                Theme.Metrics.Typography.SurfaceTitle);
            bool titlePushed = titleFont is { Available: true };
            if (titlePushed) titleFont!.Push();
            var titleSize = ImGui.CalcTextSize(title);
            dl.AddText(winMin + new Vector2(
                    Theme.Metrics.Floating.HeaderInset * scale,
                    (barHeight - titleSize.Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text)), title);
            if (titlePushed) titleFont!.Pop();

            float closeSize = Theme.Metrics.Floating.CloseAction * scale;
            ImGui.SetCursorScreenPos(new Vector2(
                winMax.X - Theme.Metrics.Floating.CloseInset * scale - closeSize,
                winMin.Y + (barHeight - closeSize) * 0.5f));
            if (FloatingSurface.CloseButton($"{id}##close"))
                keepOpen = false;

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
            ImGui.PushStyleVar(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    Theme.Metrics.Floating.ModalBodyPadding * scale,
                    Theme.Metrics.Floating.ModalBodyPadding * scale));
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
                float footerInset = Theme.Metrics.Floating.FooterInset * scale;
                float x = winMin.X + MathF.Max(
                    footerInset,
                    width - footerInset - lastWidth);
                ImGui.SetCursorScreenPos(new Vector2(
                    x,
                    footerTop + (barHeight
                        - Theme.Metrics.Control.Comfortable * scale) * 0.5f));
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
