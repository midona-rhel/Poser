using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Controls;

/// <summary>
/// Standardized spacing constants. Values are in unscaled pixels.
/// </summary>
public static class Flex
{
    public const float RowHeight = 24f;
    public const float RowSpacing = 6f;
    public const float LabelWidth = 60f;
    public const float ItemGap = 8f;
    public const float SmallGap = 4f;
    public const float ButtonWidth = 70f;
    public const float LargeIconSize = 24f;
    public const float ControlSize = 18f;
    public const float TextPadding = 6f;
    public const float ContentPadding = 6f;

    /// <summary>Compatibility shim: legacy row builder. New code should use Crystarium.Element with FlexDirection.Row.</summary>
    public static FlexRow Row(float height = RowHeight, float gap = 0, float? width = null)
        => new FlexRow(height, gap, width);
}

/// <summary>
/// Legacy horizontal flex container. Public API preserved for compat;
/// internals route through Crystarium.Element with FlexDirection.Row.
/// </summary>
public sealed class FlexRow : IDisposable
{
    private const float RowBlockSpacing = 14f;

    private readonly List<FlexItem> _items = new();
    private readonly float _heightUnscaled;
    private readonly float _widthUnscaled;
    private readonly float _gapUnscaled;
    private readonly Vector2 _startPos;

    internal FlexRow(float height, float gap, float? width)
    {
        _heightUnscaled = height;
        _gapUnscaled = gap;

        var cursorPos = ImGui.GetCursorPos();
        if (width.HasValue)
        {
            _startPos = cursorPos;
            _widthUnscaled = width.Value / PoserUI.Scale;
        }
        else
        {
            var min = ImGui.GetWindowContentRegionMin();
            float w = ImGui.GetWindowContentRegionMax().X - min.X;
            _startPos = new Vector2(min.X, cursorPos.Y);
            _widthUnscaled = w / PoserUI.Scale;
        }
    }

    private const float DefaultLabelWidth = 70f;

    public FlexRow Label(string text, float width = DefaultLabelWidth) => Fixed(width, (w, h) =>
    {
        float oy = (h - ImGui.GetTextLineHeight()) / 2f;
        if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
        float tw = ImGui.CalcTextSize(text).X;
        float ox = w - tw;
        if (ox > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ox);
        ImGui.Text(text);
    });

    public FlexRow Fixed(float width, Action draw) => Fixed(width, (_, _) => draw());
    public FlexRow Fixed(float width, Action<float, float> draw) { _items.Add(new FlexItem { IsFixed = true, SizeUnscaled = width, Draw = draw }); return this; }

    public FlexRow Fill(Action draw) => Flex(1, (_, _) => draw());
    public FlexRow Fill(Action<float> draw) => Flex(1, (w, _) => draw(w));
    public FlexRow Fill(Action<float, float> draw) => Flex(1, draw);

    public FlexRow Flex(float weight, Action draw) => Flex(weight, (_, _) => draw());
    public FlexRow Flex(float weight, Action<float> draw) => Flex(weight, (w, _) => draw(w));
    public FlexRow Flex(float weight, Action<float, float> draw) { _items.Add(new FlexItem { IsFixed = false, Weight = weight, Draw = draw }); return this; }

    public FlexRow Text(string text, float? width = null)
    {
        float tw = width ?? (ImGui.CalcTextSize(text).X / PoserUI.Scale);
        return Fixed(tw, (w, h) =>
        {
            float oy = (h - ImGui.GetTextLineHeight()) / 2f;
            if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
            ImGui.Text(text);
        });
    }

    public FlexRow Checkbox(string id, ref bool value, Action? onChanged = null)
    {
        bool localValue = value;
        return Fixed(PoserCheckbox.Size / PoserUI.Scale, (w, h) =>
        {
            float oy = (h - PoserCheckbox.Size) / 2f;
            if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
            if (PoserCheckbox.Draw(id, ref localValue)) onChanged?.Invoke();
        });
    }

    public FlexRow IconToggle(string id, ref bool value, Dalamud.Interface.FontAwesomeIcon icon, string? tooltip = null, Action? onChanged = null)
    {
        bool localValue = value;
        return Fixed(Controls.IconToggle.Size / PoserUI.Scale, (w, h) =>
        {
            float oy = (h - Controls.IconToggle.Size) / 2f;
            if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
            if (Controls.IconToggle.Draw(id, ref localValue, icon, tooltip)) onChanged?.Invoke();
        });
    }

    public FlexRow Spacer() => Fill(() => { });

    public void Dispose()
    {
        if (_items.Count == 0) { AdvanceEmpty(); return; }
        ImGui.SetCursorPos(_startPos);

        Crystarium.Element(new ElementProps
        {
            Style = new ElementStyle
            {
                FlexDirection = FlexDirection.Row,
                Width = Sizing.Fixed(_widthUnscaled),
                Height = Sizing.Fixed(_heightUnscaled),
                Gap = _gapUnscaled,
                Margin = new Spacing(0, 0, RowBlockSpacing, 0),
            },
        }, () =>
        {
            foreach (var item in _items)
            {
                var captured = item;
                Crystarium.Element(new ElementProps
                {
                    Style = new ElementStyle
                    {
                        Width = captured.IsFixed ? Sizing.Fixed(captured.SizeUnscaled) : Sizing.Flex(captured.Weight),
                    },
                }, () => captured.Draw(Crystarium.AvailableWidth, Crystarium.AvailableHeight));
            }
        });
    }

    private void AdvanceEmpty()
    {
        float scale = PoserUI.Scale;
        float nextY = _startPos.Y + (_heightUnscaled * scale) + (RowBlockSpacing * scale);
        ImGui.SetCursorPos(new Vector2(_startPos.X, nextY));
    }

    private struct FlexItem
    {
        public bool IsFixed;
        public float SizeUnscaled;
        public float Weight;
        public Action<float, float> Draw;
    }
}

public static class Layout
{
    public static float RemainingWidth => ImGui.GetContentRegionAvail().X;
    public static float RemainingHeight => ImGui.GetContentRegionAvail().Y;
    public static float ColumnWidth(int columnCount, float totalWidth = -1)
    {
        if (totalWidth < 0) totalWidth = RemainingWidth;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        return (totalWidth - spacing * (columnCount - 1)) / columnCount;
    }
    public static void CenterHorizontally(float itemWidth)
    {
        float offset = (RemainingWidth - itemWidth) / 2;
        if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
    }
    public static void CenterVertically(float itemHeight, float containerHeight)
    {
        float offset = (containerHeight - itemHeight) / 2;
        if (offset > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offset);
    }
    public static void AlignRight(float itemWidth)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + RemainingWidth - itemWidth);
    }
}
