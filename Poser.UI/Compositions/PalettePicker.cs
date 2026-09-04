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
        private const float SwatchWidth = 34f;
        private const float SwatchHeight = 22f;
        private const float Gap = 3f;
        private const float Pad = 8f;
        private const int Columns = 8;

        private readonly string _popupId;
        private readonly string _gridId;
        private readonly Action _body;
        private float _fit = 1f;
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
        }

        /// <summary>Arms the grid under the tile that owns it.</summary>
        public void Open(
            IReadOnlyList<Vector4> colors, int selected, Vector2 anchorMin, Vector2 anchorMax)
        {
            if (colors.Count == 0)
                return;
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

            int rows = (_colors.Count + Columns - 1) / Columns;
            float width = Pad * 2f + Columns * (SwatchWidth + Gap) - Gap;
            float height = Pad * 2f + rows * (SwatchHeight + Gap) - Gap;
            var display = ImGui.GetIO().DisplaySize / ImGuiHelpers.GlobalScale;
            // All native slots remain visible. Compact the whole grid together
            // at smaller display sizes; never reflow or scroll the index rows.
            _fit = MathF.Min(1f, MathF.Min(display.X / width, display.Y / height));

            _picked = null;
            FloatingSurface.Popup(
                _popupId,
                new FloatingSurfaceProps
                {
                    Width = width * _fit,
                    Height = height * _fit,
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
            float scale = ImGuiHelpers.GlobalScale * _fit;
            ImGui.SetCursorScreenPos(min + new Vector2(Pad * scale));
            DrawGrid();
            if (_picked != null)
                ImGui.CloseCurrentPopup();
        }

        private void DrawGrid()
        {
            var theme = ActiveTheme;
            float scale = ImGuiHelpers.GlobalScale * _fit;
            var origin = ImGui.GetCursorScreenPos();
            var size = new Vector2(SwatchWidth, SwatchHeight) * scale;
            var pitch = new Vector2(SwatchWidth + Gap, SwatchHeight + Gap) * scale;
            var draw = ImGui.GetWindowDrawList();
            for (int i = 0; i < _colors.Count; i++)
            {
                int row = i / Columns;
                int column = i % Columns;
                var min = origin + new Vector2(column * pitch.X, row * pitch.Y);
                ImGui.SetCursorScreenPos(min);
                var hit = Interactive.Reserve($"{_gridId}-{i}", size, false);
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
                var color = _colors[i];
                float luminance = color.X * 0.299f + color.Y * 0.587f + color.Z * 0.114f;
                TextInBand(min, size, i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new TextStyle
                    {
                        Size = theme.Typography.CaptionSize * _fit,
                        Color = luminance > 0.5f ? new Vector4(0f, 0f, 0f, 1f) : Vector4.One,
                    }, TextConstraint.Truncate(size.X, TextAlign.Center));
            }
        }
    }
}
