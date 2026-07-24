using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal static partial class Element
{
    /// <summary>
    /// <see cref="Position.Absolute"/> / <see cref="Position.Fixed"/>: anchor to
    /// the nearest positioning ancestor (or window content area for Fixed),
    /// resolve from Top/Left/Right/Bottom + Width/Height, draw out of flow.
    /// </summary>
    private static void RenderPositioned(ElementProps props, Action? children, ElementStyle resolved, Position pos)
    {
        float scale = ImGuiHelpers.GlobalScale;

        Vector2 anchorMin, anchorMax;
        if (pos == UI.Position.Fixed)
        {
            var winPos = ImGui.GetWindowPos();
            anchorMin = winPos + ImGui.GetWindowContentRegionMin();
            anchorMax = winPos + ImGui.GetWindowContentRegionMax();
        }
        else if (TryPeekPositionContext(out var pcMin, out var pcMax))
        {
            anchorMin = pcMin;
            anchorMax = pcMax;
        }
        else
        {
            var winPos = ImGui.GetWindowPos();
            anchorMin = winPos + ImGui.GetWindowContentRegionMin();
            anchorMax = winPos + ImGui.GetWindowContentRegionMax();
        }

        float top = (resolved.Top ?? 0f) * scale;
        float left = (resolved.Left ?? 0f) * scale;
        float? right = resolved.Right.HasValue ? resolved.Right.Value * scale : (float?)null;
        float? bottom = resolved.Bottom.HasValue ? resolved.Bottom.Value * scale : (float?)null;

        float width = ResolveOuterWidth(resolved.Width ?? Sizing.Fixed(0), anchorMax.X - anchorMin.X, scale);
        float height;
        if (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
            height = resolved.Height.Value.Value * scale;
        else if (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fill)
            height = anchorMax.Y - anchorMin.Y;
        else
            height = 24f * scale;

        width = ApplyMinMaxWidth(width, resolved, scale);
        height = ApplyMinMaxHeight(height, resolved, scale);

        float x = right.HasValue && !resolved.Left.HasValue ? anchorMax.X - right.Value - width : anchorMin.X + left;
        float y = bottom.HasValue && !resolved.Top.HasValue ? anchorMax.Y - bottom.Value - height : anchorMin.Y + top;

        var screenMin = new Vector2(x, y);
        var screenMax = screenMin + new Vector2(width, height);

        var savedCursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(screenMin);

        DrawChrome(screenMin, screenMax, resolved);

        PushPositionContext(screenMin, screenMax);

        var padding = resolved.Padding ?? new Spacing(0);
        int pushes = PushCascade(resolved);
        bool clipPushed = ApplyOverflowClip(resolved, screenMin, screenMax);
        try
        {
            if (children != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(screenMin.X + padding.Left * scale, screenMin.Y + padding.Top * scale));
                float innerW = width - padding.Horizontal * scale;
                float innerH = height - padding.Vertical * scale;
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerW;
                _ambientHeight = innerH;
                try
                {
                    var direction = resolved.FlexDirection ?? FlexDirection.Column;
                    if (direction == FlexDirection.Row)
                        RunRowChildren(children, innerW, innerH, resolved.Gap ?? 0f, resolved.AlignItems, resolved.FlexWrap, resolved.RowGap ?? 4f);
                    else
                        children();
                }
                finally
                {
                    _ambientWidth = prevW;
                    _ambientHeight = prevH;
                }
            }
        }
        finally
        {
            if (clipPushed) ImGui.GetWindowDrawList().PopClipRect();
            PopCascade(pushes);
            PopPositionContext();
        }

        ImGui.SetCursorScreenPos(savedCursor);
    }
}
