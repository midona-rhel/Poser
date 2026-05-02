using System;

namespace Poser.UI;

/// <summary>
/// Typed pseudo-class state. Bit-flags so an element can be in multiple states
/// (e.g. <c>Hover | Active</c>). Selector matching: a rule applies when all
/// of its required pseudos are present in the current state.
/// </summary>
[Flags]
public enum PseudoState
{
    None     = 0,
    Hover    = 1 << 0,
    Active   = 1 << 1,
    Disabled = 1 << 2,
    On       = 1 << 3,
    Checked  = 1 << 4,
    Focus    = 1 << 5,
    Open     = 1 << 6,
    Expanded = 1 << 7,
    Dragging = 1 << 8,
}

internal static class PseudoStateParser
{
    public static PseudoState Parse(string name) => name switch
    {
        "hover" => PseudoState.Hover,
        "active" => PseudoState.Active,
        "disabled" => PseudoState.Disabled,
        "on" => PseudoState.On,
        "checked" => PseudoState.Checked,
        "focus" => PseudoState.Focus,
        "open" => PseudoState.Open,
        "expanded" => PseudoState.Expanded,
        "dragging" => PseudoState.Dragging,
        _ => throw new ArgumentException($"Unknown pseudo-class: ':{name}'"),
    };
}
