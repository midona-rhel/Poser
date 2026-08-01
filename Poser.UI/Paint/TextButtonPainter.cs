using System.Numerics;

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

    public Vector4 Paint(
        in Poser.UI.InteractionResult hit, uint identity, byte paintArg, bool disabled) =>
        Poser.UI.LegacyCrystarium.PaintTextButtonBox(
            hit, identity, (Poser.UI.ButtonVariant)paintArg, disabled);
}
