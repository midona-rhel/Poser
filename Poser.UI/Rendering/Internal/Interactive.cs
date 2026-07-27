using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Hit-test result from <see cref="Interactive.Reserve"/>: the placed rect, the
/// pseudo-state derived from hover/active/disabled, and a click flag.
/// </summary>
public readonly struct InteractionResult
{
    public readonly Vector2 ScreenMin;
    public readonly Vector2 ScreenMax;
    public readonly PseudoState State;
    public readonly bool Clicked;

    public InteractionResult(Vector2 min, Vector2 max, PseudoState state, bool clicked)
    {
        ScreenMin = min;
        ScreenMax = max;
        State = state;
        Clicked = clicked;
    }

    public Vector2 Size => ScreenMax - ScreenMin;
    public bool Hovered  => (State & PseudoState.Hover)  != 0;
    public bool Active   => (State & PseudoState.Active) != 0;
    public bool Disabled => (State & PseudoState.Disabled) != 0;
}

/// <summary>
/// Reserves space, runs an <see cref="ImGui.InvisibleButton"/> hit-test, and
/// returns the resulting state. Called by every chrome tag (Button, Toggle,
/// Checkbox, ...) as the first step.
/// </summary>
public static class Interactive
{
    private readonly record struct Occluder(Vector2 Min, Vector2 Max);

    private static List<Occluder> _previousOccluders = new();
    private static List<Occluder> _currentOccluders = new();
    private static int _surfaceDepth;
    private static bool _blockRemainderOfFrame;

    public static int SurfaceDepth => _surfaceDepth;

    /// <summary>Starts the shared retained-UI interaction frame. Floating
    /// rectangles survive for one frame so surfaces drawn after their owners
    /// still occlude controls submitted earlier in the next frame.</summary>
    public static void BeginFrame()
    {
        (_previousOccluders, _currentOccluders) =
            (_currentOccluders, _previousOccluders);
        _currentOccluders.Clear();
        _surfaceDepth = 0;
        _blockRemainderOfFrame = false;
    }

    public static void BlockRemainderOfFrame() =>
        _blockRemainderOfFrame = true;

    public static void BeginSurface(Vector2 min, Vector2 max)
    {
        _currentOccluders.Add(new Occluder(min, max));
        _surfaceDepth++;
    }

    public static void RegisterOccluder(Vector2 min, Vector2 max) =>
        _currentOccluders.Add(new Occluder(min, max));

    public static void EndSurface()
    {
        if (_surfaceDepth > 0)
            _surfaceDepth--;
    }

    public static bool PointerOccluded(int registrationDepth = 0)
    {
        if (registrationDepth > 0 || _surfaceDepth > 0)
            return false;
        if (_blockRemainderOfFrame)
            return true;
        var mouse = ImGui.GetMousePos();
        foreach (var rect in _currentOccluders)
            if (Contains(rect, mouse))
                return true;
        foreach (var rect in _previousOccluders)
            if (Contains(rect, mouse))
                return true;
        return false;
    }

    private static bool Contains(in Occluder rect, Vector2 point) =>
        point.X >= rect.Min.X && point.X < rect.Max.X
        && point.Y >= rect.Min.Y && point.Y < rect.Max.Y;

    /// <summary>
    /// Reserve a hit-test rect at the current cursor.
    /// </summary>
    public static InteractionResult Reserve(
        string id, Vector2 size, bool disabled)
    {
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        var max = min + size;

        bool occluded = PointerOccluded();
        bool hovered = ImGui.IsItemHovered() && !disabled && !occluded;
        bool active  = ImGui.IsItemActive()  && !disabled && !occluded;
        bool clicked = ImGui.IsItemClicked() && !disabled && !occluded;

        PseudoState state = PseudoState.None;
        if (hovered)  state |= PseudoState.Hover;
        if (active)   state |= PseudoState.Active;
        if (disabled) state |= PseudoState.Disabled;

        return new InteractionResult(min, max, state, clicked);
    }
}
