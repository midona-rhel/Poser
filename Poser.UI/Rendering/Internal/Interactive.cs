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
    /// <summary>
    /// Reserve a hit-test rect at the current cursor.
    /// </summary>
    public static InteractionResult Reserve(
        string id, Vector2 size, bool disabled)
    {
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        var max = min + size;

        bool hovered = ImGui.IsItemHovered() && !disabled;
        bool active  = ImGui.IsItemActive()  && !disabled;
        bool clicked = ImGui.IsItemClicked() && !disabled;

        PseudoState state = PseudoState.None;
        if (hovered)  state |= PseudoState.Hover;
        if (active)   state |= PseudoState.Active;
        if (disabled) state |= PseudoState.Disabled;

        return new InteractionResult(min, max, state, clicked);
    }
}
