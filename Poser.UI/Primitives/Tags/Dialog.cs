using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>Dialog width presets (440/560/680).</summary>
public enum DialogSize
{
    Small,
    Medium,
    Large,
}

public static partial class Crystarium
{
    // Footer right-alignment uses the previous frame's measured width (standard
    // ImGui trick — avoids double-rendering children and their ID collisions).
    private static readonly Dictionary<string, float> _modalBodyHeights = new();
    private static readonly Dictionary<string, float> _modalFooterWidths = new();
    private static readonly HashSet<string> _dialogNeedsPlacement = new();

    /// <summary>
    /// A normal, movable window with the shared glass header, body and footer.
    /// It never claims exclusive input or dims the game behind it.
    /// <code>
    ///   if (Crystarium.Button("Open")) modalOpen = true;
    ///   Crystarium.Dialog("##import", modalOpen,
    ///       next => modalOpen = next, "Import pose",
    ///       body: () => { ... },
    ///       footer: () => Crystarium.Button("Import", Import,
    ///           ButtonVariant.Primary));
    /// </code>
    /// </summary>
    /// <returns>True on the frame the modal closes.</returns>
    public static bool Dialog(
        string id,
        bool open,
        Action<bool> onOpenChanged,
        string title,
        Action body,
        Action? footer = null, DialogSize size = DialogSize.Small, float? height = null,
        Vector2? position = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        string popupId = $"{title}##{id}";

        if (!open)
        {
            _dialogNeedsPlacement.Remove(popupId);
            return false;
        }

        float width = size switch
        {
            DialogSize.Medium => Crystarium.ActiveTheme.Floating.MediumWidth,
            DialogSize.Large => Crystarium.ActiveTheme.Floating.LargeWidth,
            _ => Crystarium.ActiveTheme.Floating.SmallWidth,
        } * scale;
        float barHeight = Crystarium.ActiveTheme.Floating.ModalBarHeight * scale;
        // No stated height: the modal is as tall as its body. The body's
        // content height is what the previous frame measured; a modal that
        // has never been measured draws one transparent, non-interactive
        // frame, so the user only sees its final size.
        bool measured = _modalBodyHeights.TryGetValue(popupId, out float measuredBody);
        bool measuringFrame = height is null && !measured;
        float totalHeight = height is { } stated
            ? stated * scale
            : measured
                ? MathF.Min(
                    barHeight + measuredBody + (footer != null ? barHeight : 0f),
                    ImGui.GetIO().DisplaySize.Y - 2f * barHeight)
                : Crystarium.ActiveTheme.Floating.DefaultModalHeight * scale;
        float rounding = Crystarium.ActiveTheme.Radii.Surface * scale;

        // After measurement, place once. Subsequent content
        // changes must not recenter the window or undo the user's dragging.
        bool placeAfterMeasurement = _dialogNeedsPlacement.Remove(popupId);
        if (measuringFrame)
            _dialogNeedsPlacement.Add(popupId);
        ImGui.SetNextWindowPos(
            position ?? FloatingSurface.PlaceCentered(new Vector2(width, totalHeight)),
            measuringFrame || placeAfterMeasurement ? ImGuiCond.Always : ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, totalHeight));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, measuringFrame ? 0f : ImGui.GetStyle().Alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f); // border trio drawn manually

        bool closedThisFrame = false;
        bool keepOpen = open;
        bool visible = ImGui.Begin(popupId, ref keepOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | (measuringFrame ? ImGuiWindowFlags.NoInputs : ImGuiWindowFlags.None));
        // The unwind is unconditional (PBI-013 class): a throw in the body
        // or footer callback must not skip End or strand the
        // style entries on the global stack for every window drawn after.
        try
        {
            if (visible)
                DrawOpenModal();
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(1);
        }
        if (!keepOpen)
        {
            onOpenChanged(false);
            closedThisFrame = true;
        }
        return closedThisFrame;

        void DrawOpenModal()
        {
            var dl = ImGui.GetWindowDrawList();
            var winMin = ImGui.GetWindowPos();
            var winMax = winMin + ImGui.GetWindowSize();
            var modalOwner = Interactive.BeginOwner(
                popupId,
                InteractionLayer.Window,
                winMin,
                winMax);
            try
            {
                DrawModalContent(dl, winMin, winMax);
            }
            finally
            {
                Interactive.EndOwner(modalOwner);
            }
        }

        void DrawModalContent(
            ImDrawListPtr dl, Vector2 winMin, Vector2 winMax)
        {
            bool canDismissWithEscape = DialogHasKeyboardFocus();
            FloatingSurface.DrawChrome(
                dl,
                winMin,
                winMax,
                Crystarium.ActiveTheme.Radii.Surface);

            var theme = Crystarium.ActiveTheme;

            // ── Header: title 14px/500 at 16px, close 24×24 at right 10px,
            //    inset bottom border (border-secondary). Ink-seated on the
            //    bar band through the canonical path.
            float headerInset = Crystarium.ActiveTheme.Floating.HeaderInset * scale;
            Crystarium.TextInBand(
                new Vector2(winMin.X + headerInset, winMin.Y),
                new Vector2(winMax.X - winMin.X - headerInset, barHeight),
                title,
                new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.SurfaceTitleSize,
                    Weight = FontWeight.Medium,
                    Color = theme.Text,
                });

            float closeSize = Crystarium.ActiveTheme.Floating.CloseActionSize * scale;
            ImGui.SetCursorScreenPos(winMin);
            var titleDrag = Interactive.Reserve(
                $"{id}##move",
                new Vector2(width - closeSize - theme.Floating.CloseInset * scale, barHeight),
                disabled: false);
            if (titleDrag.DragDelta != Vector2.Zero)
                ImGui.SetWindowPos(winMin + titleDrag.DragDelta);

            ImGui.SetCursorScreenPos(new Vector2(
                winMax.X - Crystarium.ActiveTheme.Floating.CloseInset * scale - closeSize,
                winMin.Y + (barHeight - closeSize) * 0.5f));
            if (FloatingSurface.CloseButton($"{id}##close"))
                keepOpen = false;

            dl.AddRectFilled(
                new Vector2(winMin.X, winMin.Y + barHeight - 1f * scale),
                new Vector2(winMax.X, winMin.Y + barHeight),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.WeakOverlay)));

            // ── Footer chrome (drawn before body so its strip sits under nothing)
            float footerTop = winMax.Y - barHeight;
            if (footer != null)
            {
                dl.AddRectFilled(new Vector2(winMin.X, footerTop), winMax,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.ModalFooter)),
                    rounding, ImDrawFlags.RoundCornersBottom);
                dl.AddRectFilled(
                    new Vector2(winMin.X, footerTop),
                    new Vector2(winMax.X, footerTop + 1f * scale),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.WeakOverlay)));
            }

            // ── Body: padding 16, scrollable between the bars.
            float bodyHeight = totalHeight - barHeight - (footer != null ? barHeight : 0f);
            ImGui.SetCursorScreenPos(winMin + new Vector2(0f, barHeight));
            ImGui.PushStyleVar(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    Crystarium.ActiveTheme.Floating.ModalBodyPadding * scale,
                    Crystarium.ActiveTheme.Floating.ModalBodyPadding * scale));
            // AlwaysUseWindowPadding: borderless children ignore WindowPadding otherwise.
            bool bodyVisible = ImGui.BeginChild(
                $"{id}##body", new Vector2(width, bodyHeight), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysUseWindowPadding);
            try
            {
                if (bodyVisible)
                {
                    body();
                    // What the body used, padding on both sides included;
                    // the trailing item spacing is not content.
                    float padding = Crystarium.ActiveTheme.Floating.ModalBodyPadding * scale;
                    _modalBodyHeights[popupId] = MathF.Max(
                        2f * padding,
                        ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + padding);
                }
            }
            finally
            {
                ImGui.EndChild();
                ImGui.PopStyleVar();
            }

            // ── Footer content: right-aligned via last frame's measured width.
            if (footer != null)
            {
                _modalFooterWidths.TryGetValue(popupId, out float lastWidth);
                float footerInset = Crystarium.ActiveTheme.Floating.FooterInset * scale;
                float x = winMin.X + MathF.Max(
                    footerInset,
                    width - footerInset - lastWidth);
                ImGui.SetCursorScreenPos(new Vector2(
                    x,
                    footerTop + (barHeight
                        - Crystarium.ActiveTheme.Controls.ComfortableHeight * scale) * 0.5f));
                ImGui.BeginGroup();
                footer();
                ImGui.EndGroup();
                _modalFooterWidths[popupId] = ImGui.GetItemRectSize().X;
            }

            if (canDismissWithEscape && DialogHasKeyboardFocus()
                && ImGui.IsKeyPressed(ImGuiKey.Escape, repeat: false))
                keepOpen = false;
        }
    }

    internal static bool DialogHasKeyboardFocus() =>
        ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
        && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup);
}
