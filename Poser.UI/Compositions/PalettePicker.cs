using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// One colour out of a fixed palette — a skin, hair or eye colour the
    /// game offers — chosen off a grid of the colours themselves. Retained
    /// like <see cref="TexturePicker"/> and drained the same way: the row
    /// that opened it reads the pick from <see cref="Draw"/>. The tile that
    /// opens it is the caller's (a <c>ColorTile</c>); the picker owns only
    /// the popup and the pick.
    /// </summary>
    public sealed class PalettePicker
    {
        private const float Swatch = 18f;
        private const float Gap = 3f;
        private const float Pad = 8f;
        private const int Columns = 12;
        private const int VisibleRows = 8;

        private readonly string _popupId;
        private readonly string _gridId;
        private readonly Action _body;
        private readonly Action<ScrollRegionScope> _grid;
        private IReadOnlyList<Vector4> _colors = Array.Empty<Vector4>();
        private int _selected = -1;
        private Vector2 _anchorMin;
        private Vector2 _anchorMax;
        private bool _openRequested;
        private int? _picked;

        public PalettePicker(string id)
        {
            _popupId = $"##palette-picker-{id}";
            _gridId = $"{_popupId}-grid";
            _body = DrawBody;
            _grid = DrawGrid;
        }

        /// <summary>Arms the grid under the tile that owns it.</summary>
        public void Open(
            IReadOnlyList<Vector4> colors, int selected, Vector2 anchorMin, Vector2 anchorMax)
        {
            _colors = colors;
            _selected = selected;
            _anchorMin = anchorMin;
            _anchorMax = anchorMax;
            _openRequested = true;
        }

        /// <summary>Runs the surface and answers the picked index once.</summary>
        public int? Draw()
        {
            if (_openRequested)
            {
                _openRequested = false;
                OpenPopover(_popupId);
            }
            if (!ImGui.IsPopupOpen(_popupId))
                return null;

            var theme = ActiveTheme;
            int rows = Math.Max(1, (_colors.Count + Columns - 1) / Columns);
            float pitch = Swatch + Gap;
            float width = Pad + Columns * pitch - Gap + theme.Scrollbar.GutterWidth;
            float height = Pad * 2f + Math.Min(rows, VisibleRows) * pitch - Gap;

            _picked = null;
            FloatingSurface.Popup(
                _popupId,
                new FloatingSurfaceProps
                {
                    Width = width,
                    Height = height,
                    Padding = 0f,
                    AnchorMin = _anchorMin,
                    AnchorMax = _anchorMax,
                    Treatment = FloatingSurfaceTreatment.Glass,
                },
                _body);
            return _picked;
        }

        private void DrawBody()
        {
            var min = ImGui.GetWindowPos();
            float scale = ImGuiHelpers.GlobalScale;
            int rows = Math.Max(1, (_colors.Count + Columns - 1) / Columns);
            float pitch = Swatch + Gap;
            ImGui.SetCursorScreenPos(min + new Vector2(Pad * scale));
            ScrollRegion(
                _gridId,
                Columns * pitch - Gap + ActiveTheme.Scrollbar.GutterWidth,
                Math.Min(rows, VisibleRows) * pitch - Gap,
                _grid);
            if (_picked != null)
                ImGui.CloseCurrentPopup();
        }

        private void DrawGrid(ScrollRegionScope region)
        {
            var theme = ActiveTheme;
            float scale = ImGuiHelpers.GlobalScale;
            var origin = ImGui.GetCursorScreenPos();
            float side = Swatch * scale;
            float pitch = (Swatch + Gap) * scale;
            var draw = ImGui.GetWindowDrawList();
            int rows = Math.Max(1, (_colors.Count + Columns - 1) / Columns);
            for (int i = 0; i < _colors.Count; i++)
            {
                int row = i / Columns;
                int column = i % Columns;
                var min = origin + new Vector2(column * pitch, row * pitch);
                ImGui.SetCursorScreenPos(min);
                var hit = Interactive.Reserve($"{_gridId}-{i}", new Vector2(side), false);
                bool active = i == _selected;
                BoxRenderer.Draw(draw, hit.ScreenMin, hit.ScreenMax, new BoxStyle
                {
                    BackgroundColor = _colors[i] with { W = 1f },
                    BorderRadius = theme.Radii.Control * 0.5f,
                    BorderWidth = active ? 1.5f : 0f,
                    BorderTopColor = active ? theme.Chrome.Primary : null,
                    BorderRightColor = active ? theme.Chrome.Primary : null,
                    BorderBottomColor = active ? theme.Chrome.Primary : null,
                    BorderLeftColor = active ? theme.Chrome.Primary : null,
                });
                if (hit.Hovered || hit.Active)
                    BoxRenderer.Draw(draw, hit.ScreenMin, hit.ScreenMax, new BoxStyle
                    {
                        BackgroundColor = theme.Chrome.WeakOverlay,
                        BorderRadius = theme.Radii.Control * 0.5f,
                    });
                if (hit.Clicked)
                    _picked = i;
            }
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, rows * pitch - Gap * scale));
            ImGui.Dummy(new Vector2(1f, 1f));
        }
    }
}
