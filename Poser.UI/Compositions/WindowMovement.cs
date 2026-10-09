using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    private static uint _titleDragId;
    private static Vector2 _titleDragOffset;

    /// <summary>Call after title controls. The host uses NoMove so content cannot start a native drag.</summary>
    public static void WindowTitleDrag(Vector2 min, Vector2 max)
    {
        var cursor = ImGui.GetCursorScreenPos();
        uint id = ImGui.GetID("##window-title-drag");
        bool controlHovered = _titleDragId != id && ImGui.IsAnyItemHovered();
        ImGui.SetCursorScreenPos(min);
        var drag = Interactive.Reserve("##window-title-drag", max - min, disabled: controlHovered);
        if (drag.DragBegan)
        {
            _titleDragId = id;
            _titleDragOffset = ImGui.GetMousePos() - ImGui.GetWindowPos();
        }
        // Anchor to the press, not this frame's delta: movement before the
        // press must not move the window, and skipped frames cannot accumulate drift.
        if (_titleDragId == id && drag.Active && !drag.DragBegan
            && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(ImGui.GetMousePos() - _titleDragOffset);
        if (drag.DragEnded && _titleDragId == id)
            _titleDragId = 0;
        ImGui.SetCursorScreenPos(cursor);
    }
}
