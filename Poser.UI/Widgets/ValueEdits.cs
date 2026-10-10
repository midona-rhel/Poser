using System;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Widgets;

/// <summary>Raises the value-edit seam on <see cref="UiContext"/>: edit
/// began and ended around a change, commit when a control commits.</summary>
public static class ValueEdits
{
    public static void ChangeValue(string id, Action change)
    {
        var ui = UiContext.Current;
        ui.RaiseValueEditBegan(ImGui.GetID(id));
        try { change(); }
        finally { ui.RaiseValueEditEnded(); }
    }

    // A control can disappear on a tab/window change before drawing its
    // deactivation frame. Commit its authored value once interaction is idle.
    public static void EndValueFrame()
    {
        if (!ImGui.IsAnyItemActive() && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UiContext.Current.RaiseValueEditingIdle();
    }

    /// <summary>The control's own commit callback, then the shared seam.</summary>
    public static void Commit(string id, Action? onCommit = null)
    {
        onCommit?.Invoke();
        UiContext.Current.RaiseValueCommitted(ImGui.GetID(id));
    }
}
