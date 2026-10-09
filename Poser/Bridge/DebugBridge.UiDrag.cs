#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private sealed class DragProbe(Vector2 from, Vector2 to, int frames)
    {
        public readonly Vector2 From = from, To = to;
        public readonly int Frames = frames;
        public readonly DateTime Deadline = DateTime.UtcNow.AddSeconds(8);
        public int Step, LastFrame = -1;
    }

    private DragProbe? _uiDrag;
    private readonly object _uiDragGate = new();
    private bool _restoreUiEvents;
    private readonly List<object> _uiDragTrace = new();

    private string UiDrag(Dictionary<string, string> query)
    {
        lock (_uiDragGate)
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                return Json(new { error = "The bridge is shutting down." });
            return UiDragLocked(query);
        }
    }

    private string UiDragLocked(Dictionary<string, string> query)
    {
        if (query.GetValueOrDefault("cancel") == "1") FinishUiDrag();
        if (query.ContainsKey("x"))
        {
            if (_uiDrag != null) return Json(new { error = "A gesture is already running." });
            float Read(string key) => float.Parse(query[key], CultureInfo.InvariantCulture);
            var from = new Vector2(Read("x"), Read("y"));
            var to = new Vector2(Read("toX"), Read("toY"));
            if (!float.IsFinite(from.X) || !float.IsFinite(from.Y)
                || !float.IsFinite(to.X) || !float.IsFinite(to.Y))
                return Json(new { error = "Coordinates must be finite." });
            int frames = Math.Clamp(int.Parse(query.GetValueOrDefault("frames", "12")), 2, 60);
            _restoreUiEvents = ImGui.GetIO().AppAcceptingEvents;
            _uiDragTrace.Clear();
            _uiDrag = new(from, to, frames);
        }
        return Json(new { running = _uiDrag != null, step = _uiDrag?.Step,
            acceptingEvents = ImGui.GetIO().AppAcceptingEvents, trace = _uiDragTrace });
    }

    private void AdvanceUiDrag(IFramework framework)
    {
        // Requests already run on the framework thread; disposal need not.
        // Serialize it with an in-flight step so a finished probe cannot
        // re-press the button and suppress native events after cleanup.
        lock (_uiDragGate) AdvanceUiDragLocked();
    }

    private void AdvanceUiDragLocked()
    {
        if (System.Threading.Volatile.Read(ref _disposed) != 0)
        {
            FinishUiDragLocked();
            return;
        }
        if (_uiDrag is not { } probe) return;
        if (DateTime.UtcNow >= probe.Deadline) { FinishUiDrag(); return; }
        int frame = ImGui.GetFrameCount();
        if (probe.LastFrame == frame) return;
        probe.LastFrame = frame;
        var io = ImGui.GetIO();
        var hoveredWindow = ImGui.GetCurrentContext().HoveredWindow;
        _uiDragTrace.Add(new { step = probe.Step, frame,
            x = io.MousePos.X, y = io.MousePos.Y, down = ImGui.IsMouseDown(ImGuiMouseButton.Left),
            focusLost = io.AppFocusLost, dragging = ImGui.IsMouseDragging(ImGuiMouseButton.Left),
            hoveredWindow = hoveredWindow.IsNull ? null : new { id = hoveredWindow.ID,
                x = hoveredWindow.Pos.X, y = hoveredWindow.Pos.Y } });
        // The desktop backend otherwise replaces each injected position on the
        // following frame. Isolate this bounded diagnostic gesture only; restore
        // native event admission after release, timeout, cancel or plugin disposal.
        io.SetAppAcceptingEvents(true);
        float progress = Math.Clamp((probe.Step - 3f) / probe.Frames, 0f, 1f);
        var position = Vector2.Lerp(probe.From, probe.To, progress);
        io.AddMousePosEvent(position.X, position.Y);
        io.AddMouseButtonEvent(0, probe.Step >= 3 && probe.Step <= probe.Frames + 3);
        io.SetAppAcceptingEvents(false);
        if (probe.Step++ >= probe.Frames + 6) FinishUiDrag();
    }

    private void FinishUiDrag()
    {
        lock (_uiDragGate) FinishUiDragLocked();
    }

    private void FinishUiDragLocked()
    {
        if (_uiDrag == null) return;
        var io = ImGui.GetIO();
        io.SetAppAcceptingEvents(true);
        io.AddMouseButtonEvent(0, false);
        io.SetAppAcceptingEvents(_restoreUiEvents);
        _uiDrag = null;
    }
}
#endif
