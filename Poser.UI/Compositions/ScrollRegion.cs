using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
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
                width - Crystarium.ActiveTheme.Scrollbar.GutterWidth);
            content(new ScrollRegionScope(contentWidth, scale));
        }
        NarrowVisibleScrollbarThumb();
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
            string? badge = null,
            IDalamudTextureWrap? iconTexture = null,
            bool iconVisible = true,
            ControlStyle style = default)
        {
            DrawSeparator();
            _lastRowMin = ImGui.GetCursorScreenPos();
            _lastRowMax = _lastRowMin + new Vector2(
                ContentWidth * _scale,
                Crystarium.ActiveTheme.Controls.ListRowHeight * _scale);
            bool clicked = SidebarRow(
                id,
                label,
                new SidebarRowProps
                {
                    Icon = icon,
                    NoExpanderSlot = true,
                    Selected = selected,
                    Badge = badge,
                    IconTexture = iconTexture,
                    HideIcon = !iconVisible,
                },
                style with { Width = UiWidth.Fixed(ContentWidth) });
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
                    Crystarium.ActiveTheme.Spacing.Four,
                    Crystarium.ActiveTheme.Spacing.Three) * _scale;
            DrawText(
                origin,
                ContentWidth * _scale,
                Crystarium.ActiveTheme.Typography.CaptionSize,
                FontWeight.Regular,
                FormHintColor,
                text);
            ImGui.Dummy(new Vector2(
                ContentWidth * _scale,
                Crystarium.ActiveTheme.Controls.ListRowHeight * _scale));
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
                    Crystarium.ActiveTheme.Border with { W = 0.24f }));
        }
    }

    private static void PushScrollbarStyle()
    {
        float scale = ImGuiHelpers.GlobalScale;
        var text = Crystarium.ActiveTheme.Text;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ScrollbarSize,
            Crystarium.ActiveTheme.Scrollbar.GutterWidth * scale);
        ImGui.PushStyleVar(
            ImGuiStyleVar.ScrollbarRounding,
            Crystarium.ActiveTheme.Scrollbar.Radius * scale);
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

    private static void PopScrollbarStyle()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
    }

    /// <summary>Keeps the canonical gutter and hit target intact while
    /// narrowing only ImGui's emitted visible grab geometry.</summary>
    private static unsafe void NarrowVisibleScrollbarThumb()
    {
        var draw = ImGui.GetWindowDrawList();
        float scale = ImGuiHelpers.GlobalScale;
        float gutter = Crystarium.ActiveTheme.Scrollbar.GutterWidth * scale;
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X;
        float left = right - gutter;
        float center = (left + right) * 0.5f;
        uint normal = ImGui.GetColorU32(ImGuiCol.ScrollbarGrab);
        uint hovered = ImGui.GetColorU32(ImGuiCol.ScrollbarGrabHovered);
        uint active = ImGui.GetColorU32(ImGuiCol.ScrollbarGrabActive);
        var vertices = (ImDrawVert*)draw.VtxBuffer.Data;
        for (int i = 0; i < draw.VtxBuffer.Size; i++)
        {
            ref var vertex = ref vertices[i];
            if (vertex.Pos.X < left || vertex.Pos.X > right)
                continue;
            if (vertex.Col != normal
                && vertex.Col != hovered
                && vertex.Col != active)
                continue;
            vertex.Pos.X = center + (vertex.Pos.X - center) * 0.8f;
        }
    }
}
