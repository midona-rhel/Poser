using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal static partial class Element
{
    /// <summary>
    /// Render path for elements whose rect was assigned by a parent row or grid.
    /// The cell width/height are passed in; chrome paints into that rect; child
    /// content draws inside the padded area.
    /// </summary>
    private static void RenderInline(ElementProps props, Action? children, float width, float height)
    {
        var screenMin = ImGui.GetCursorScreenPos();
        var screenMax = screenMin + new Vector2(width, height);

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;

        bool interactive = props.OnClick != null || props.OnContextMenu != null
                        || props.OnDragStart != null || props.OnDrop != null;
        bool clicked = false;
        bool dragHover = false;
        if (interactive && props.Id != null)
        {
            ImGui.SetCursorScreenPos(screenMin);
            ImGui.InvisibleButton(props.Id, new Vector2(width, height));
            if (ImGui.IsItemHovered()) state |= PseudoState.Hover;
            if (ImGui.IsItemActive()) state |= PseudoState.Active;
            if (ImGui.IsItemClicked()) clicked = true;

            // Drag source — opens BeginDragDropSource if hover+drag.
            DragDropAdapter.TrySource(props.OnDragStart, props.Tooltip ?? props.Id);

            // Drop target — accepts payload if hovered + valid type.
            dragHover = DragDropAdapter.TryTarget(props.OnDrop, props.CanAcceptDrop);

            ImGui.SetCursorScreenPos(screenMin);
        }

        var resolved = Stylesheet.Resolve(props.Classes, props.Id, state).MergedWith(props.Style);
        if (resolved.Display == UI.Display.None) return;

        // Apply drag-hover background tint (CSS-shaped: ElementStyle.DragHoverColor wins over BackgroundColor).
        if (dragHover && resolved.DragHoverColor.HasValue)
            resolved.BackgroundColor = resolved.DragHoverColor;

        var padding = resolved.Padding ?? new Spacing(0);
        var direction = resolved.FlexDirection ?? FlexDirection.Column;
        var gap = resolved.Gap ?? 0f;

        // Capture vertex range so a non-identity Transform can rewrite it after rendering.
        var xformDrawList = ImGui.GetWindowDrawList();
        int xformVtxStart = (resolved.Transform.HasValue && !resolved.Transform.Value.IsIdentity) ? xformDrawList.VtxBuffer.Size : -1;

        // Switch draw channel for z-index (no-op when ZIndex is null/zero).
        int prevChannel = -1;
        if (resolved.ZIndex.HasValue && resolved.ZIndex.Value != 0)
            prevChannel = LayerManager.Switch(xformDrawList, resolved.ZIndex.Value);

        DrawChrome(screenMin, screenMax, resolved);

        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenMin, screenMax);

        int pushes = PushCascade(resolved);
        bool clipPushed = ApplyOverflowClip(resolved, screenMin, screenMax);
        try
        {
            if (children != null)
            {
                float scale = ImGuiHelpers.GlobalScale;
                float innerW = width - padding.Horizontal * scale;
                float innerH = height - padding.Vertical * scale;
                ImGui.SetCursorScreenPos(new Vector2(screenMin.X + padding.Left * scale, screenMin.Y + padding.Top * scale));

                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerW;
                _ambientHeight = innerH;
                try
                {
                    if (direction == FlexDirection.Row)
                        RunRowChildren(children, innerW, innerH, gap, resolved.AlignItems, resolved.FlexWrap, resolved.RowGap ?? 4f);
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
            if (isPositioningContext) PopPositionContext();
        }

        // Apply transform to all vertices captured during chrome + content rendering.
        if (xformVtxStart >= 0 && resolved.Transform is { } xform)
        {
            int xformVtxEnd = xformDrawList.VtxBuffer.Size;
            VertexTransform.Apply(xformDrawList, xformVtxStart, xformVtxEnd, screenMin, screenMax, xform);
        }

        // Restore the previous draw channel.
        if (prevChannel >= 0) LayerManager.Restore(xformDrawList, prevChannel);

        ImGui.SetCursorScreenPos(screenMin);
        ImGui.Dummy(new Vector2(width, height));

        if (interactive)
        {
            if (clicked && !props.Disabled) props.OnClick?.Invoke();
            if (!string.IsNullOrEmpty(props.Tooltip) && (state & PseudoState.Hover) != 0)
                Crystarium.HoverHelp.Explain(props.Id ?? props.Tooltip!,
                    ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), props.Tooltip!);

            if ((state & PseudoState.Hover) != 0)
                ApplyCursor(resolved.Cursor ?? UI.Cursor.Pointer);
        }
        else if (resolved.Cursor.HasValue && (state & PseudoState.Hover) != 0)
        {
            ApplyCursor(resolved.Cursor.Value);
        }
    }
}
