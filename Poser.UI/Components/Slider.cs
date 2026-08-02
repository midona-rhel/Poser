using System;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The range slider. CONTROLLED: the element carries the range and the value's
/// normalized position, the base turns a drag into the value under the pointer,
/// and the caller decides what to do with it.
/// </summary>
public readonly record struct Slider
{
    public float Value { get; init; }

    public float Min { get; init; }

    public float Max { get; init; }

    public UiHandler<float> OnChange { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public ElementSheet? StyleSheet { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Slider control) => (UiNode)control;

    public static implicit operator UiNode(Slider control) => new Element
    {
        Sheet = SheetFamily.Slider,
        Style = control.StyleSheet,
        Value = control.Max > control.Min
            ? Math.Clamp(
                (control.Value - control.Min) / (control.Max - control.Min), 0f, 1f)
            : 0f,
        On = new Listeners
        {
            OnDrag = control.OnChange,
            Min = control.Min,
            Max = control.Max,
        },
        Painter = SliderPainter.Instance,
        Disabled = control.Disabled,
        Help = control.Help,
        Key = control.Key,
    };
}
