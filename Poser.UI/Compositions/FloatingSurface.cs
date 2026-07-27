using System;
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
            Norvrandt.Box(min, max, new BoxStyle
            {
                BorderWidth = 1f,
                BorderRadius = radius,
                BorderTopColor = ActiveTheme.Glass.BorderTop,
                BorderLeftColor = ActiveTheme.Glass.BorderSide,
                BorderRightColor = ActiveTheme.Glass.BorderSide,
                BorderBottomColor = ActiveTheme.Glass.BorderBottom,
            });

        public static bool CloseButton(string id) =>
            IconButtonTablerCore(
                TablerIcon.X,
                Cls.SurfaceClose, id, null, null, false, null);

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
                var min = ImGui.GetWindowPos();
                DrawChrome(
                    ImGui.GetWindowDrawList(),
                    min,
                    min + ImGui.GetWindowSize(),
                    Crystarium.ActiveTheme.Radii.Surface);
                body();
                ImGui.EndPopup();
            }

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
                DrawChrome(
                    ImGui.GetWindowDrawList(),
                    min,
                    max,
                    Crystarium.ActiveTheme.Radii.Window);
                body(new FloatingSurfaceFrame(min, max, scale));
            }
            ImGui.End();
            ImGui.PopStyleVar(2);
            return visible;
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

        internal static void DrawChrome(
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
