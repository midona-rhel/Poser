using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// Picto's ColorPalette: the dark pill holding a run of 16px swatch wraps.
/// Each dot's colour is its sheet fill, its rings the one swatch paint the
/// imperative control uses; selection is the element's, and a name rides as
/// the dot's own help.
/// </summary>
public readonly record struct Swatches
{
    public required IReadOnlyList<Vector4> Colors { get; init; }

    public int Selected { get; init; }

    public UiHandler<int> OnChange { get; init; }

    public IReadOnlyList<string>? Names { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Swatches swatches) =>
        (UiNode)swatches;

    public static implicit operator UiNode(Swatches swatches) =>
        swatches.Emit();

    private UiNode Emit()
    {
        IReadOnlyList<Vector4> colors = Colors;
        Span<UiNode> dots = FrameArena.Require().ScratchNodes(colors.Count);
        for (int i = 0; i < colors.Count; i++)
        {
            dots[i] = new Element
            {
                Sheet = SheetFamily.SwatchBox,
                Style = Element.Tinted(colors[i]),
                Painter = SwatchPainter.Instance,
                Selected = i == Selected,
                Index = i,
                On = new Listeners { OnPick = OnChange },
                Help = Names is { } names && i < names.Count ? names[i] : null,
                Key = i,
            };
        }

        return new Row
        {
            Sheet = SheetFamily.SwatchPalette,
            Key = Key,
            Children = UiChildren.Create(dots),
        };
    }
}
