using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public static void ScrollRegion(
        string id,
        float width,
        float height,
        Action<ScrollRegionScope> content)
    {
        float scale = ImGuiHelpers.GlobalScale;
        PushScrollbarStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool visible = ImGui.BeginChild(
            id,
            new Vector2(width * scale, height * scale),
            false,
            ImGuiWindowFlags.NoSavedSettings);
        if (visible)
        {
            float contentWidth = MathF.Max(
                0f,
                ImGui.GetContentRegionAvail().X / scale
                    - Theme.Metrics.Scrollbar.Gutter);
            content(new ScrollRegionScope(contentWidth, scale));
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        PopScrollbarStyle();
    }

    public sealed class ScrollRegionScope
    {
        private readonly float _scale;
        private int _rowCount;
        private Vector2 _lastRowMin;
        private Vector2 _lastRowMax;

        internal ScrollRegionScope(float contentWidth, float scale)
        {
            ContentWidth = contentWidth;
            _scale = scale;
        }

        public float ContentWidth { get; }

        public bool ListRow(
            string id,
            string label,
            TablerIcon icon = TablerIcon.Circle,
            bool selected = false,
            string? badge = null)
        {
            DrawSeparator();
            _lastRowMin = ImGui.GetCursorScreenPos();
            _lastRowMax = _lastRowMin + new Vector2(
                ContentWidth * _scale,
                Theme.Metrics.Control.ListRow * _scale);
            bool clicked = SidebarRow(
                id,
                label,
                new SidebarRowProps
                {
                    Icon = icon,
                    NoExpanderSlot = true,
                    Selected = selected,
                    Badge = badge,
                    Width = ContentWidth,
                });
            _rowCount++;
            return clicked;
        }

        public bool LastRowDoubleClicked() =>
            ImGui.IsWindowHovered()
            && ImGui.IsMouseHoveringRect(_lastRowMin, _lastRowMax)
            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

        public void Empty(string text)
        {
            var origin = ImGui.GetCursorScreenPos()
                + new Vector2(
                    Theme.Metrics.Space.Four,
                    Theme.Metrics.Space.Three) * _scale;
            DrawText(
                origin,
                ContentWidth * _scale,
                Theme.Metrics.Typography.Caption,
                FontWeight.Regular,
                FormHintColor,
                text);
            ImGui.Dummy(new Vector2(
                ContentWidth * _scale,
                Theme.Metrics.Control.ListRow * _scale));
        }

        private void DrawSeparator()
        {
            if (_rowCount == 0)
                return;
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(
                cursor,
                new Vector2(
                    cursor.X + ContentWidth * _scale,
                    cursor.Y + MathF.Max(1f, _scale)),
                ImGui.ColorConvertFloat4ToU32(
                    Norvrandt.Sheet.CurrentTheme.Border with { W = 0.24f }));
        }
    }

    public static void PushScrollbarStyle()
    {
        float scale = ImGuiHelpers.GlobalScale;
        var text = Norvrandt.Sheet.CurrentTheme.Text;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ScrollbarSize,
            Theme.Metrics.Scrollbar.Gutter * scale);
        ImGui.PushStyleVar(
            ImGuiStyleVar.ScrollbarRounding,
            Theme.Metrics.Scrollbar.Radius * scale);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(
            ImGuiCol.ScrollbarGrab,
            text with { W = 0.12f });
        ImGui.PushStyleColor(
            ImGuiCol.ScrollbarGrabHovered,
            text with { W = 0.25f });
        ImGui.PushStyleColor(
            ImGuiCol.ScrollbarGrabActive,
            text with { W = 0.25f });
    }

    public static void PopScrollbarStyle()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
    }
}
