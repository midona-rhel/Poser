using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The colour well. The value it shows is its sheet's FILL, so a well states a
/// colour the way anything else does; the picker inside its popover is the
/// named native boundary and edits inline.
/// </summary>
public readonly record struct ColorWell
{
    public Vector4 Color { get; init; }

    public UiHandler<Vector4> OnChange { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(ColorWell well) => (UiNode)well;

    public static implicit operator UiNode(ColorWell well) => new Element
    {
        Sheet = SheetFamily.ColorWell,
        Style = Element.Tinted(well.Color),
        On = new Listeners { OnColor = well.OnChange },
        Painter = ColorWellPainter.Instance,
        Disabled = well.Disabled,
        Help = well.Help,
        Key = well.Key,
    };
}
