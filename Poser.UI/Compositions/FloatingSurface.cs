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
        // This affects surface chrome only; controls keep their own paint.
        public static void ConfigureEffects(float fillOpacity, bool backdropBlur) =>
            GlassChrome.Configure(fillOpacity, backdropBlur);

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
        internal static void OpenPopup(string id)
        {
            Interactive.ClaimExclusive(id);
            ImGui.OpenPopup(id);
        }
        internal static bool SyncExclusive(string id)
        {
            Interactive.TouchExclusive(id);
            return Interactive.OwnsExclusive(id);
        }
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
            // End and pop every Begin-time resource in reverse order, even when the body fails.
            try
            {
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
                        try
                        {
                            if (props.Treatment == FloatingSurfaceTreatment.Glass)
                                DrawChrome(
                                    ImGui.GetWindowDrawList(),
                                    min,
                                    max,
                                    Crystarium.ActiveTheme.Radii.Surface);
                            body();
                        }
                        finally
                        {
                            Interactive.EndOwner(owner);
                        }
                    }
                }
            }
            finally
            {
                if (open)
                    ImGui.EndPopup();
                ReleaseWhenClosed(id, ImGui.IsPopupOpen(id));
                ImGui.PopStyleColor();
                ImGui.PopStyleVar(3);
            }
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
            // The owner and style stack always unwind with their matching Begin call.
            try
            {
                if (visible)
                {
                    var min = ImGui.GetWindowPos();
                    var max = min + ImGui.GetWindowSize();
                    var owner = Interactive.BeginOwner(
                        id, InteractionLayer.FloatingWindow, min, max);
                    try
                    {
                        DrawChrome(
                            ImGui.GetWindowDrawList(),
                            min,
                            max,
                            Crystarium.ActiveTheme.Radii.Window);
                        body(new FloatingSurfaceFrame(min, max, scale));
                    }
                    finally
                    {
                        Interactive.EndOwner(owner);
                    }
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(2);
            }
            ReleaseWhenClosed(id, open);
            return visible;
        }
        public static int HoverList(
            string id,
            Vector2 anchor,
            IReadOnlyList<string> items,
            int selected,
            InteractionLayer layer = InteractionLayer.HoverSurface,
            bool onTop = false,
            float? width = null)
        {
            if (items.Count == 0)
                return -1;
            float scale = ImGuiHelpers.GlobalScale;
            float gutter = ActiveTheme.Scrollbar.GutterWidth * 0.5f;
            float padding = gutter * scale;
            float labelSize = ActiveTheme.Typography.BodySize;
            // A caller that holds the list open across changing entries
            // passes the width it measured once, so the surface never
            // resizes under the pointer; entries wider than it truncate.
            float listWidth = width ?? HoverListWidth(items);
            float rowHeight = labelSize + ActiveTheme.Spacing.Two * 2f;
            int rows = Math.Min(items.Count, ActiveTheme.Picker.MaximumRows);
            float height = rows * rowHeight * scale
                + padding * 2f;
            var requested = anchor + new Vector2(
                ActiveTheme.Floating.AnchorGap * scale,
                0f);
            var min = PlaceAtPoint(
                requested,
                new Vector2(listWidth, height),
                scale,
                out _);
            var max = min + new Vector2(listWidth, height);

            ImGui.SetNextWindowPos(min);
            ImGui.SetNextWindowSize(max - min);
            ImGui.PushStyleVar(
                ImGuiStyleVar.WindowPadding, new Vector2(0f, padding));
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
                // On top: above the overlays that draw after it, the gizmo's
                // window included — a list beside the pointer is never
                // under anything.
                if (onTop)
                    ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
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
                ImGui.SetCursorPosX(padding);
                ScrollRegion(
                    $"{id}-rows",
                    (listWidth - padding) / scale,
                    (height - padding * 2f) / scale,
                    region =>
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (region.ListRow(
                                    $"{id}-row-{i}",
                                    items[i],
                                    selected: i == selected,
                                    iconVisible: false,
                                    style: new ControlStyle
                                    {
                                        Height = UiHeight.Fixed(rowHeight),
                                    },
                                    labelSize: labelSize))
                                clicked = i;
                            // The highlighted entry is the one the wheel
                            // moves: the list scrolls to keep it in view.
                            if (i == selected)
                                ImGui.SetScrollHereY(0.5f);
                        }
                    },
                    gutterWidth: gutter);
                Interactive.EndOwner(owner);
            }
            ImGui.End();
            ImGui.PopStyleVar();
            return clicked;
        }

        /// <summary>The width the hover list takes for these entries:
        /// the widest label within the menu's bounds. Screen pixels.</summary>
        public static float HoverListWidth(IReadOnlyList<string> items)
        {
            float scale = ImGuiHelpers.GlobalScale;
            float padding = ActiveTheme.Scrollbar.GutterWidth * 0.5f * scale;
            var labelStyle = new TextStyle
            {
                Size = ActiveTheme.Typography.BodySize,
            };
            float widest = 0f;
            for (int i = 0; i < items.Count; i++)
                widest = MathF.Max(widest, MeasureText(items[i], labelStyle).X);
            return Math.Clamp(
                widest + padding * 2f + ActiveTheme.Spacing.Two * scale,
                ActiveTheme.Floating.MenuMinWidth * scale,
                ActiveTheme.Floating.MenuWidth * scale);
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
            float radius,
            bool shadow = true,
            bool blur = true,
            bool fill = true,
            bool border = true,
            float fade = 1f)
        {
            float scale = ImGuiHelpers.GlobalScale;
            if (blur)
                GlassChrome.PrependBlur(
                    drawList, min, max, radius * scale, fade);
            drawList.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
            BoxRenderer.Draw(
                drawList,
                min,
                max,
                new BoxStyle
                {
                    BackgroundColor = fill
                        ? GlassChrome.BackgroundColor
                        : (Vector4?)null,
                    BorderWidth = border ? 1f : 0f,
                    BorderRadius = radius,
                    BorderTopColor = border
                        ? Crystarium.ActiveTheme.Glass.BorderTop
                        : (Vector4?)null,
                    BorderLeftColor = border
                        ? Crystarium.ActiveTheme.Glass.BorderSide
                        : (Vector4?)null,
                    BorderRightColor = border
                        ? Crystarium.ActiveTheme.Glass.BorderSide
                        : (Vector4?)null,
                    BorderBottomColor = border
                        ? Crystarium.ActiveTheme.Glass.BorderBottom
                        : (Vector4?)null,
                    BoxShadows = shadow
                        ?
                        [
                            Crystarium.ActiveTheme.Shadows.Panel,
                            Crystarium.ActiveTheme.Shadows.PanelRing,
                        ]
                        : null,
                });
            drawList.PopClipRect();
        }
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
