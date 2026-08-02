namespace Poser.UI.Reactive;

/// <summary>
/// Bridges the retained tree to the canonical text-button box. A singleton,
/// because a painter carries no per-element state: the paint argument is the
/// variant byte the declaration stored, and the label is a real child.
/// </summary>
internal sealed class TextButtonPainter : IInteractivePainter
{
    internal static readonly TextButtonPainter Instance = new();

    private TextButtonPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input) =>
        // A button carries no glyph opacity of its own: the box states none,
        // so its content stays on whatever a nested icon asked for.
        new(
            Poser.UI.LegacyCrystarium.PaintTextButtonBox(
                input.Hit,
                input.Identity,
                (Poser.UI.ButtonVariant)input.Arg,
                input.Disabled),
            1f);
}
