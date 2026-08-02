using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Declares a NATIVE island: a leaf that reserves an arranged rectangle and
    /// lets imperative ImGui draw inside it. The tree keeps what it is good at
    /// — the box, the identity, the place in the flow — and gives up what it
    /// cannot own: a text field's caret, selection, clipboard and IME
    /// composition all live in ImGui's own retained widget state.
    ///
    /// <para><paramref name="element"/> is RETAINED by the caller, not
    /// synthesized per frame, so a warm frame costs nothing.</para>
    ///
    /// <para>Internal for the same reason <see cref="Portal"/> is: a caller
    /// that reached for one directly would be opting out of layout, paint and
    /// identity all at once.</para>
    /// </summary>
    internal static UiNode Native(
        INativeElement element, Vector2 logicalSize, UiKey key = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new Element
        {
            Style = Element.Sized(
                UiDim.Fixed(logicalSize.X), UiDim.Fixed(logicalSize.Y)),
            Native = element,
            Key = key,
        };
    }
}
