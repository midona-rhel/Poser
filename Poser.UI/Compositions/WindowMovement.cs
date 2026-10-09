using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Call after title controls. The host uses NoMove so content cannot start a native drag.</summary>
    public static void WindowTitleDrag(Vector2 min, Vector2 max)
    {
        var cursor = ImGui.GetCursorScreenPos();
        bool controlHovered = ImGui.IsAnyItemHovered();
        ImGui.SetCursorScreenPos(min);
        var drag = Interactive.Reserve("##window-title-drag", max - min, disabled: controlHovered);
        if (drag.DragDelta != Vector2.Zero)
            ImGui.SetWindowPos(ImGui.GetWindowPos() + drag.DragDelta);
        ImGui.SetCursorScreenPos(cursor);
    }
}
