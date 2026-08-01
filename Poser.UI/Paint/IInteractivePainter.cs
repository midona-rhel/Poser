using System.Numerics;

namespace Poser.UI.Reactive;

/// <summary>
/// The paint half of an interactive element, kept OUT of the walk: the root
/// resolves identity, reserves the rect and hands both to a retained painter
/// singleton, so no element kind is special-cased in the runtime. The return
/// value is the subtree's resolved foreground (currentColor semantics) — the
/// painter decides what its content should be tinted with, and every Text
/// below the element inherits that unless it names its own color.
/// </summary>
internal interface IInteractivePainter
{
    Vector4 Paint(
        in Poser.UI.InteractionResult hit, uint identity, byte paintArg, bool disabled);
}
