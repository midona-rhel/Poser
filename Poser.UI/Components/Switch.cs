using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The iOS-style toggle. CONTROLLED: the element carries the value it is
/// SHOWING as <c>Selected</c>, and the base reports its negation — so the
/// control owns no state and states no dispatch rule of its own.
/// </summary>
public readonly record struct Switch
{
    public bool Value { get; init; }

    public UiHandler<bool> OnToggle { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public ElementSheet? StyleSheet { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Switch control) => (UiNode)control;

    public static implicit operator UiNode(Switch control) => new Element
    {
        Sheet = SheetFamily.Switch,
        Style = control.StyleSheet,
        Selected = control.Value,
        On = new Listeners { OnToggle = control.OnToggle },
        Painter = SwitchPainter.Instance,
        Disabled = control.Disabled,
        Help = control.Help,
        Key = control.Key,
    };
}
