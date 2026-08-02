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
    /// synthesized per frame: the arena's object slot holds a reference, so a
    /// warm frame costs nothing, and the island's own per-frame inputs are
    /// written into its fields exactly as a portal body's are.</para>
    ///
    /// <para>Internal for the same reason <see cref="Portal"/> is: an island is
    /// a control's own machinery. A caller that reached for one directly would
    /// be opting out of layout, paint and identity all at once, and the escape
    /// hatch is only sound where a control has already decided it needs it.
    /// </para>
    /// </summary>
    /// <param name="logicalSize">The island's intrinsic box, declared the way
    /// an interactive leaf declares its own. The solver still resolves a Fill
    /// or Fixed dimension over it.</param>
    internal static UiNode Native(
        INativeElement element, Vector2 logicalSize, UiKey key = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        FrameArena arena = FrameArena.Require();
        ElementRecord record = default;
        record.Kind = ElementKind.Native;
        record.Key = key;
        record.LogicalSize = logicalSize;
        record.NativeSlot = arena.AddObject(element);
        return arena.AddElement(record);
    }
}
