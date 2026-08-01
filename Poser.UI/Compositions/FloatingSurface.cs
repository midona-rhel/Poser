using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public record struct FloatingSurfaceProps
{
    public float Width;
    public float Height;
    public float Padding;
    public Vector2 AnchorMin;
    public Vector2 AnchorMax;
    public FloatingSurfaceTreatment Treatment;
}

public enum FloatingSurfaceTreatment
{
    Glass,
    Unframed,
}

public static partial class Crystarium
{
    public static class FloatingSurface
    {
        public static bool BackdropBlurAvailable
        {
            get => GlassChrome.BackdropBlurAvailable;
            set => GlassChrome.BackdropBlurAvailable = value;
        }

        public static Vector4 FillColor => GlassChrome.BackgroundColor;

        public static void PrependShellBlur(
            ImDrawListPtr drawList,
            Vector2 min,
            Vector2 max,
            float rounding) =>
            GlassChrome.PrependBlur(drawList, min, max, rounding);

        public static void DrawBorder(Vector2 min, Vector2 max, float radius) =>
            BoxRenderer.Draw(ImGui.GetWindowDrawList(), min, max, new BoxStyle
            {
                BorderWidth = 1f,
                BorderRadius = radius,
                BorderTopColor = ActiveTheme.Glass.BorderTop,
                BorderLeftColor = ActiveTheme.Glass.BorderSide,
                BorderRightColor = ActiveTheme.Glass.BorderSide,
                BorderBottomColor = ActiveTheme.Glass.BorderBottom,
            });

        public static bool CloseButton(string id) =>
            IconButton(
                TablerIcon.X,
                style: ControlStyle.Square(
                    ActiveTheme.Floating.CloseActionSize),
                id: id);

        /// <summary>
        /// The implementation behind <see cref="Crystarium.OpenPopover"/>:
        /// claims the exclusive chain BEFORE ImGui's popup stack, so the
        /// surface owns input from the frame it opens. Deliberately
        /// internal — <see cref="Crystarium.OpenPopover"/> is the one
        /// public open path, and hiding this makes that compile-enforced
        /// for every assembly outside Poser.UI.
        /// </summary>
        internal static void OpenPopup(string id)
        {
            Interactive.ClaimExclusive(id);
            ImGui.OpenPopup(id);
        }

        /// <summary>
        /// One step of the exclusive-chain handshake: marks the surface
        /// alive for this frame and reports whether it still owns its link
        /// in the chain. False means an outer surface superseded it and the
        /// caller must close — touching a surface that no longer exists is
        /// a no-op, so the order inside is immaterial to the caller.
        /// </summary>
        internal static bool SyncExclusive(string id)
        {
            Interactive.TouchExclusive(id);
            return Interactive.OwnsExclusive(id);
        }

        /// <summary>
        /// The closing half of the handshake: releases the surface's link
        /// (and everything nested under it) once it is no longer open.
        /// Returns true when it released, so callers can bail in the same
        /// statement.
        /// </summary>
        internal static bool ReleaseWhenClosed(string id, bool open)
        {
            if (open)
                return false;
            Interactive.ReleaseExclusive(id);
            return true;
        }

        public static void OpenWindow(string id) =>
            Interactive.ClaimExclusive(
                id, InteractionLayer.FloatingWindow);

        public static bool Popup(
            string id,
            in FloatingSurfaceProps props,
            Action body)
        {
            float scale = ImGuiHelpers.GlobalScale;
            var size = new Vector2(props.Width, props.Height) * scale;
            var position = PlaceAnchored(
                props.AnchorMin,
                props.AnchorMax,
                size,
                scale);
            float padding = props.Padding * scale;
            float radius = Crystarium.ActiveTheme.Radii.Surface * scale;

            ImGui.SetNextWindowPos(position);
            ImGui.SetNextWindowSize(size);
            ImGui.PushStyleVar(
                ImGuiStyleVar.WindowPadding,
                new Vector2(padding, padding));
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, radius);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);

            bool open = ImGui.BeginPopup(
                id,
                ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoSavedSettings);
            if (open)
            {
                bool owns = SyncExclusive(id);
                var min = ImGui.GetWindowPos();
                var max = min + ImGui.GetWindowSize();
                if (!owns)
                {
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    var owner = Interactive.BeginOwner(
                        id, InteractionLayer.Popup, min, max);
                    if (props.Treatment == FloatingSurfaceTreatment.Glass)
                        DrawChrome(
                            ImGui.GetWindowDrawList(),
                            min,
                            max,
                            Crystarium.ActiveTheme.Radii.Surface);
                    body();
                    Interactive.EndOwner(owner);
                }
                ImGui.EndPopup();
            }
            ReleaseWhenClosed(id, ImGui.IsPopupOpen(id));

            ImGui.PopStyleColor();
            ImGui.PopStyleVar(3);
            return open;
        }

        public static bool Window(
            string id,
            ref bool open,
            float width,
            float height,
            Action<FloatingSurfaceFrame> body)
        {
            if (open && !SyncExclusive(id))
            {
                open = false;
                return false;
            }
            if (ReleaseWhenClosed(id, open))
                return false;
            float scale = ImGuiHelpers.GlobalScale;
            var size = new Vector2(width, height) * scale;
            ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(
                PlaceCentered(size),
                ImGuiCond.Appearing);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            bool visible = ImGui.Begin(
                id,
                ref open,
                ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoResize);
            if (visible)
            {
                var min = ImGui.GetWindowPos();
                var max = min + ImGui.GetWindowSize();
                var owner = Interactive.BeginOwner(
                    id, InteractionLayer.FloatingWindow, min, max);
                DrawChrome(
                    ImGui.GetWindowDrawList(),
                    min,
                    max,
                    Crystarium.ActiveTheme.Radii.Window);
                body(new FloatingSurfaceFrame(min, max, scale));
                Interactive.EndOwner(owner);
            }
            ImGui.End();
            ImGui.PopStyleVar(2);
            // The window's own close button clears `open` mid-frame.
            ReleaseWhenClosed(id, open);
            return visible;
        }

        public static int HoverList(
            string id,
            Vector2 anchor,
            IReadOnlyList<string> items,
            int selected,
            InteractionLayer layer = InteractionLayer.HoverSurface)
        {
            if (items.Count == 0)
                return -1;
            float scale = ImGuiHelpers.GlobalScale;
            float padding = ActiveTheme.Floating.PopupPadding * scale;
            float width = ActiveTheme.Floating.MenuWidth * scale;
            int rows = Math.Min(items.Count, ActiveTheme.Picker.MaximumRows);
            float height = rows * ActiveTheme.Controls.ListRowHeight * scale
                + padding * 2f;
            var requested = anchor + new Vector2(
                ActiveTheme.Floating.AnchorGap * scale,
                0f);
            var min = PlaceAtPoint(
                requested,
                new Vector2(width, height),
                scale,
                out _);
            var max = min + new Vector2(width, height);

            ImGui.SetNextWindowPos(min);
            ImGui.SetNextWindowSize(max - min);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
            bool visible = ImGui.Begin(
                id,
                ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoDecoration
                | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoFocusOnAppearing);
            int clicked = -1;
            if (visible)
            {
                var owner = Interactive.BeginOwner(
                    id,
                    layer,
                    min,
                    max);
                DrawChrome(
                    ImGui.GetWindowDrawList(),
                    min,
                    max,
                    ActiveTheme.Radii.Surface);
                ImGui.SetNextFrameWantCaptureMouse(true);
                ScrollRegion(
                    $"{id}-rows",
                    (width - padding * 2f) / scale,
                    (height - padding * 2f) / scale,
                    region =>
                    {
                        for (int i = 0; i < items.Count; i++)
                            if (region.ListRow(
                                    $"{id}-row-{i}",
                                    items[i],
                                    selected: i == selected))
                                clicked = i;
                    });
                Interactive.EndOwner(owner);
            }
            ImGui.End();
            ImGui.PopStyleVar();
            return clicked;
        }

        internal static Vector2 PlaceCentered(Vector2 size) =>
            (ImGui.GetIO().DisplaySize - size) * 0.5f;

        internal static Vector2 PlaceAtPoint(
            Vector2 requested,
            Vector2 size,
            float scale,
            out Vector2 pivot)
        {
            float margin = Crystarium.ActiveTheme.Floating.ViewportInset * scale;
            var display = ImGui.GetIO().DisplaySize;
            var position = requested;
            bool fromRight = false;
            bool fromBottom = false;
            if (position.X + size.X > display.X - margin)
            {
                position.X = display.X - size.X - margin;
                fromRight = true;
            }
            if (position.Y + size.Y > display.Y - margin)
            {
                position.Y = display.Y - size.Y - margin;
                fromBottom = true;
            }
            position.X = MathF.Max(position.X, margin);
            position.Y = MathF.Max(position.Y, margin);
            pivot = new Vector2(
                fromRight ? position.X + size.X : position.X,
                fromBottom ? position.Y + size.Y : position.Y);
            return position;
        }

        public static void DrawChrome(
            ImDrawListPtr drawList,
            Vector2 min,
            Vector2 max,
            float radius)
        {
            float scale = ImGuiHelpers.GlobalScale;
            GlassChrome.PrependBlur(drawList, min, max, radius * scale);

            // Popup draw lists clip to their window. Temporarily widen only
            // the chrome clip so the canonical panel shadow remains visible.
            drawList.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
            BoxRenderer.Draw(
                drawList,
                min,
                max,
                new BoxStyle
                {
                    BackgroundColor = GlassChrome.BackgroundColor,
                    BorderWidth = 1f,
                    BorderRadius = radius,
                    BorderTopColor = Crystarium.ActiveTheme.Glass.BorderTop,
                    BorderLeftColor = Crystarium.ActiveTheme.Glass.BorderSide,
                    BorderRightColor = Crystarium.ActiveTheme.Glass.BorderSide,
                    BorderBottomColor = Crystarium.ActiveTheme.Glass.BorderBottom,
                    BoxShadows =
                    [
                        Crystarium.ActiveTheme.Shadows.Panel,
                        Crystarium.ActiveTheme.Shadows.PanelRing,
                    ],
                });
            drawList.PopClipRect();
        }

        /// <summary>
        /// Places a surface on a preferred SIDE of a semantic target:
        /// centred on that side at <paramref name="offset"/>, flipped to
        /// the opposite side when the viewport edge is closer than the
        /// surface, then clamped into the viewport. The third placement
        /// rule alongside <see cref="PlaceAnchored"/> (below/above an
        /// anchor) and <see cref="PlaceAtPoint"/> (shift-clamp a point).
        /// </summary>
        internal static Vector2 PlaceSide(
            HoverHelpSide side,
            Vector2 targetMin,
            Vector2 targetMax,
            Vector2 size,
            float offset)
        {
            var display = ImGui.GetIO().DisplaySize;
            var targetCenter = (targetMin + targetMax) * 0.5f;
            Vector2 pos = side switch
            {
                HoverHelpSide.Top => new Vector2(targetCenter.X - size.X * 0.5f, targetMin.Y - offset - size.Y),
                HoverHelpSide.Left => new Vector2(targetMin.X - offset - size.X, targetCenter.Y - size.Y * 0.5f),
                HoverHelpSide.Right => new Vector2(targetMax.X + offset, targetCenter.Y - size.Y * 0.5f),
                _ => new Vector2(targetCenter.X - size.X * 0.5f, targetMax.Y + offset),
            };
            switch (side)
            {
                case HoverHelpSide.Bottom when pos.Y + size.Y > display.Y:
                    pos.Y = targetMin.Y - offset - size.Y;
                    break;
                case HoverHelpSide.Top when pos.Y < 0f:
                    pos.Y = targetMax.Y + offset;
                    break;
                case HoverHelpSide.Right when pos.X + size.X > display.X:
                    pos.X = targetMin.X - offset - size.X;
                    break;
                case HoverHelpSide.Left when pos.X < 0f:
                    pos.X = targetMax.X + offset;
                    break;
            }
            pos.X = Math.Clamp(pos.X, 0f, MathF.Max(0f, display.X - size.X));
            pos.Y = Math.Clamp(pos.Y, 0f, MathF.Max(0f, display.Y - size.Y));
            return pos;
        }

        private static Vector2 PlaceAnchored(
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            float scale)
        {
            var display = ImGui.GetIO().DisplaySize;
            float gap = Crystarium.ActiveTheme.Floating.AnchorGap * scale;
            float x = Math.Clamp(
                anchorMin.X,
                0f,
                MathF.Max(0f, display.X - size.X));
            float y = anchorMax.Y + gap;
            if (y + size.Y > display.Y)
            {
                float above = anchorMin.Y - size.Y - gap;
                y = above >= 0f
                    ? above
                    : MathF.Max(0f, display.Y - size.Y);
            }
            return new Vector2(x, y);
        }
    }

    public readonly record struct FloatingSurfaceFrame(
        Vector2 Min,
        Vector2 Max,
        float Scale)
    {
        public Vector2 Size => Max - Min;
    }
}
