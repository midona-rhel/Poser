using System;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Widgets;

/// <summary>Reports value-edit lifecycle events through <see cref="UiContext"/>:
/// an edit begins and ends around each change, and commits with the control.</summary>
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

    /// <summary>Runs the control's commit callback, then reports the shared commit event.</summary>
    public static void Commit(string id, Action? onCommit = null)
    {
        onCommit?.Invoke();
        UiContext.Current.RaiseValueCommitted(ImGui.GetID(id));
    }
}
