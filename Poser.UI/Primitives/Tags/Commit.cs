using System;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Raised when a slider, well or field commits: the drag released, the
    /// typed value accepted on leaving the field. The value journal seals
    /// its open step here, so a drag is one undo step from press to
    /// release, a typed edit is one step from focus to unfocus, and the
    /// next touch of the same control opens a new step (ruled 2026-09-03).
    /// </summary>
    public static event Action<object>? ValueCommitted;
    public static event Action<object>? ValueEditBegan;
    public static event Action? ValueEditEnded;
    public static event Action? ValueEditingIdle;

    public static void ChangeValue(string id, Action change)
    {
        ValueEditBegan?.Invoke(ImGui.GetID(id));
        try { change(); }
        finally { ValueEditEnded?.Invoke(); }
    }

    // A control can disappear on a tab/window change before drawing its
    // deactivation frame. Commit its authored value once interaction is idle.
    public static void EndValueFrame()
    {
        if (!ImGui.IsAnyItemActive() && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            ValueEditingIdle?.Invoke();
    }

    /// <summary>The control's own commit callback, then the shared seam.</summary>
    public static void Commit(string id, Action? onCommit = null)
    {
        onCommit?.Invoke();
        ValueCommitted?.Invoke(ImGui.GetID(id));
    }
}
