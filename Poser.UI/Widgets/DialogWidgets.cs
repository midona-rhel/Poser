using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class DialogWidgets
{
    /// <summary>Measured dialog parts from the previous frame and dialogs still to be placed.</summary>
    internal sealed class DialogState
    {
        // Footer right-alignment uses the previous frame's measured width (standard
        // ImGui trick — avoids double-rendering children and their ID collisions).
        internal readonly Dictionary<string, float> BodyHeights = new();
        internal readonly Dictionary<string, float> FooterWidths = new();
        internal readonly HashSet<string> NeedsPlacement = new();
    }

    private static DialogState State => UiContext.Current.Dialogs;

    /// <summary>
    /// A normal, movable window with the shared glass header, body and footer.
    /// It never claims exclusive input or dims the game behind it.
    /// <code>
    ///   if (Button("Open")) modalOpen = true;
    ///   Dialog("##import", modalOpen,
    ///       next => modalOpen = next, "Import pose",
    ///       body: () => { ... },
    ///       footer: () => Button("Import", Import,
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
            State.NeedsPlacement.Remove(popupId);
            return false;
        }

        float width = size switch
        {
            DialogSize.Medium => ActiveTheme.Floating.MediumWidth,
            DialogSize.Large => ActiveTheme.Floating.LargeWidth,
            _ => ActiveTheme.Floating.SmallWidth,
        } * scale;
        float barHeight = ActiveTheme.Floating.ModalBarHeight * scale;
        // Auto-height settles after the first visible frame. Do not hide the
        // measurement frame with Alpha=0: ImGui skips that window's contents,
        // so no height is recorded and the dialog would stay invisible forever.
        bool measured = State.BodyHeights.TryGetValue(popupId, out float measuredBody);
        bool measuringFrame = height is null && !measured;
        float totalHeight = height is { } stated
            ? stated * scale
            : measured
                ? MathF.Min(
                    barHeight + measuredBody + (footer != null ? barHeight : 0f),
                    ImGui.GetIO().DisplaySize.Y - 2f * barHeight)
                : ActiveTheme.Floating.DefaultModalHeight * scale;
        float rounding = ActiveTheme.Radii.Surface * scale;

        // After measurement, place once. Subsequent content
        // changes must not recenter the window or undo the user's dragging.
        bool placeAfterMeasurement = State.NeedsPlacement.Remove(popupId);
        if (measuringFrame)
            State.NeedsPlacement.Add(popupId);
        ImGui.SetNextWindowPos(
            position ?? FloatingSurface.PlaceCentered(new Vector2(width, totalHeight)),
            measuringFrame || placeAfterMeasurement ? ImGuiCond.Always : ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, totalHeight));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f); // border trio drawn manually

        bool closedThisFrame = false;
        bool keepOpen = open;
        bool visible = ImGui.Begin(popupId, ref keepOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);
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
            ImGui.PopStyleVar(3);
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
                ActiveTheme.Radii.Surface);

            var theme = ActiveTheme;

            // ── Header: title 14px/500 at 16px, close 24×24 at right 10px,
            //    inset bottom border (border-secondary). Ink-seated on the
            //    bar band through the canonical path.
            float headerInset = ActiveTheme.Floating.HeaderInset * scale;
            TextInBand(
                new Vector2(winMin.X + headerInset, winMin.Y),
                new Vector2(winMax.X - winMin.X - headerInset, barHeight),
                title,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.SurfaceTitleSize,
                    Weight = FontWeight.Medium,
                    Color = theme.Text,
                });

            float closeSize = ActiveTheme.Floating.CloseActionSize * scale;
            ImGui.SetCursorScreenPos(winMin);
            var titleDrag = Interactive.Reserve(
                $"{id}##move",
                new Vector2(width - closeSize - theme.Floating.CloseInset * scale, barHeight),
                disabled: false);
            if (titleDrag.DragDelta != Vector2.Zero)
                ImGui.SetWindowPos(winMin + titleDrag.DragDelta);

            ImGui.SetCursorScreenPos(new Vector2(
                winMax.X - ActiveTheme.Floating.CloseInset * scale - closeSize,
                winMin.Y + (barHeight - closeSize) * 0.5f));
            if (FloatingSurface.CloseButton($"{id}##close"))
                keepOpen = false;

            dl.AddRectFilled(
                new Vector2(winMin.X, winMin.Y + barHeight - 1f * scale),
                new Vector2(winMax.X, winMin.Y + barHeight),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveTheme.Chrome.WeakOverlay)));

            // ── Footer chrome (drawn before body so its strip sits under nothing)
            float footerTop = winMax.Y - barHeight;
            if (footer != null)
            {
                dl.AddRectFilled(new Vector2(winMin.X, footerTop), winMax,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveTheme.Chrome.ModalFooter)),
                    rounding, ImDrawFlags.RoundCornersBottom);
                dl.AddRectFilled(
                    new Vector2(winMin.X, footerTop),
                    new Vector2(winMax.X, footerTop + 1f * scale),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveTheme.Chrome.WeakOverlay)));
            }

            // ── Body: padding 16, scrollable between the bars.
            float bodyHeight = totalHeight - barHeight - (footer != null ? barHeight : 0f);
            ImGui.SetCursorScreenPos(winMin + new Vector2(0f, barHeight));
            ImGui.PushStyleVar(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    ActiveTheme.Floating.ModalBodyPadding * scale,
                    ActiveTheme.Floating.ModalBodyPadding * scale));
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
                    float padding = ActiveTheme.Floating.ModalBodyPadding * scale;
                    State.BodyHeights[popupId] = MathF.Max(
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
                State.FooterWidths.TryGetValue(popupId, out float lastWidth);
                float footerInset = ActiveTheme.Floating.FooterInset * scale;
                float x = winMin.X + MathF.Max(
                    footerInset,
                    width - footerInset - lastWidth);
                ImGui.SetCursorScreenPos(new Vector2(
                    x,
                    footerTop + (barHeight
                        - ActiveTheme.Controls.ComfortableHeight * scale) * 0.5f));
                ImGui.BeginGroup();
                footer();
                ImGui.EndGroup();
                State.FooterWidths[popupId] = ImGui.GetItemRectSize().X;
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
