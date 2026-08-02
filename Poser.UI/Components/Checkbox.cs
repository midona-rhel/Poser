using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The form checkbox. <c>Value</c> is the element's <c>Selected</c> and the
/// paint is the one Tags seam, exactly the switch's split: state on the
/// element, pixels in the shared implementation, toggle negation the base's.
/// </summary>
public readonly record struct Checkbox
{
    public bool Value { get; init; }

    public UiHandler<bool> OnToggle { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Checkbox checkbox) =>
        (UiNode)checkbox;

    public static implicit operator UiNode(Checkbox checkbox) => new Element
    {
        Sheet = SheetFamily.Checkbox,
        Selected = checkbox.Value,
        On = new Listeners { OnToggle = checkbox.OnToggle },
        Painter = FormCheckboxPainter.Instance,
        Disabled = checkbox.Disabled,
        Help = checkbox.Help,
        Key = checkbox.Key,
    };
}
