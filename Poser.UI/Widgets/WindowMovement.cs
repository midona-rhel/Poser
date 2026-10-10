using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Widgets;

public static class WindowMovement
{
    /// <summary>The window whose title strip is being dragged.</summary>
    internal sealed class TitleDragState
    {
        internal uint Id;
        internal Vector2 Offset;
    }

    private static TitleDragState State => UiContext.Current.TitleDrag;

    internal static bool WindowTitleControlHovered()
    {
        // IsAnyItemHovered also includes last frame's item. After hovering
        // our drag strip, that would make the strip disable itself forever.
        uint hovered = ImGui.GetCurrentContext().HoveredId;
        return hovered != 0 && hovered != ImGui.GetID("##window-title-drag");
    }

    /// <summary>Call after title controls. The host uses NoMove so content cannot start a native drag.</summary>
    public static void WindowTitleDrag(Vector2 min, Vector2 max)
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) State.Id = 0;
        var cursor = ImGui.GetCursorScreenPos();
        uint id = ImGui.GetID("##window-title-drag");
        bool controlHovered = State.Id != id && WindowTitleControlHovered();
        ImGui.SetCursorScreenPos(min);
        var drag = Interactive.Reserve("##window-title-drag", max - min, disabled: controlHovered);
        if (drag.DragBegan)
        {
            State.Id = id;
            State.Offset = ImGui.GetMousePos() - ImGui.GetWindowPos();
        }
        // Anchor to the press, not this frame's delta: movement before the
        // press must not move the window, and skipped frames cannot accumulate drift.
        // Once accepted, crossing another surface cannot revoke this gesture.
        if (State.Id == id && !drag.DragEnded && !drag.DragBegan
            && ImGui.IsMouseDown(ImGuiMouseButton.Left)
            && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(ImGui.GetMousePos() - State.Offset);
        if (drag.DragEnded && State.Id == id)
            State.Id = 0;
        ImGui.SetCursorScreenPos(cursor);
    }
}
