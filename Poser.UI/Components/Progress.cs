using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The determinate bar. Purely presentational — it carries no listeners, so it
/// reserves nothing and reports nothing.
/// </summary>
public readonly record struct Progress
{
    public float Fraction { get; init; }

    /// <summary>Unset leaves the span to the solver: a form row hands the bar
    /// what its readout and actions did not take, and that is not knowable
    /// where the bar is declared.</summary>
    public UiDim Width { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Progress bar) => (UiNode)bar;

    public static implicit operator UiNode(Progress bar) => new Element
    {
        Sheet = SheetFamily.ProgressTrack,
        Style = Element.Sized(bar.Width, null),
        Value = bar.Fraction,
        Painter = ProgressPainter.Instance,
    };
}
