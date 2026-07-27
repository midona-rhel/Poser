using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>
/// Entry point for the legacy <see cref="FlexRow"/> builder. New code should use
/// <see cref="Crystarium.Element"/> directly.
/// </summary>
public static class Flex
{
    public static FlexRow Row(float? height = null, float gap = 0, float? width = null)
        => new FlexRow(height ?? Theme.Metrics.Control.FormRow, gap, width);
}

/// <summary>
/// Small compatibility row used by the retained file browser. It projects its
/// collected cells through the shared element renderer.
/// </summary>
public sealed class FlexRow : IDisposable
{
    private const float DefaultLabelWidth = 70f;

    private readonly List<FlexItem> _items = new();
    private readonly float _heightUnscaled;
    private readonly float _widthUnscaled;
    private readonly float _gapUnscaled;
    private readonly Vector2 _startPosition;

    internal FlexRow(float height, float gap, float? width)
    {
        _heightUnscaled = height;
        _gapUnscaled = gap;

        var cursor = ImGui.GetCursorPos();
        if (width.HasValue)
        {
            _startPosition = cursor;
            _widthUnscaled = width.Value / ImGuiHelpers.GlobalScale;
        }
        else
        {
            var min = ImGui.GetWindowContentRegionMin();
            var available =
                ImGui.GetWindowContentRegionMax().X - min.X;
            _startPosition = new Vector2(min.X, cursor.Y);
            _widthUnscaled = available / ImGuiHelpers.GlobalScale;
        }
    }

    public FlexRow Label(string text, float width = DefaultLabelWidth) =>
        Fixed(width, (cellWidth, cellHeight) =>
        {
            var y = (cellHeight - ImGui.GetTextLineHeight()) / 2f;
            if (y > 0)
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + y);
            var x = cellWidth - ImGui.CalcTextSize(text).X;
            if (x > 0)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + x);
            ImGui.Text(text);
        });

    public FlexRow Fixed(float width, Action draw) =>
        Fixed(width, (_, _) => draw());

    public FlexRow Fixed(float width, Action<float, float> draw)
    {
        _items.Add(new FlexItem
        {
            IsFixed = true,
            SizeUnscaled = width,
            Draw = draw,
        });
        return this;
    }

    public FlexRow Fill(Action draw) => Flex(1f, (_, _) => draw());
    public FlexRow Fill(Action<float> draw) => Flex(1f, (width, _) => draw(width));
    public FlexRow Fill(Action<float, float> draw) => Flex(1f, draw);

    public FlexRow Flex(float weight, Action<float, float> draw)
    {
        _items.Add(new FlexItem
        {
            Weight = weight,
            Draw = draw,
        });
        return this;
    }

    public FlexRow Spacer() => Fill(() => { });

    public void Dispose()
    {
        if (_items.Count == 0)
            return;

        ImGui.SetCursorPos(_startPosition);
        Norvrandt.Element(new ElementProps
        {
            Style = new ElementStyle
            {
                FlexDirection = FlexDirection.Row,
                Width = Sizing.Fixed(_widthUnscaled),
                Height = Sizing.Fixed(_heightUnscaled),
                Gap = _gapUnscaled,
            },
        }, () =>
        {
            foreach (var item in _items)
            {
                var captured = item;
                Norvrandt.Element(new ElementProps
                {
                    Style = new ElementStyle
                    {
                        Width = captured.IsFixed
                            ? Sizing.Fixed(captured.SizeUnscaled)
                            : Sizing.Flex(captured.Weight),
                    },
                }, () => captured.Draw(
                    Norvrandt.AvailableWidth,
                    Norvrandt.AvailableHeight));
            }
        });
    }

    private struct FlexItem
    {
        public bool IsFixed;
        public float SizeUnscaled;
        public float Weight;
        public required Action<float, float> Draw;
    }
}
