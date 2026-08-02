using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// The single seam between the retained tree and the imperative input kernel.
/// The tree already knows where an element sits, so reservation is a cursor
/// placement plus the ordinary <see cref="Poser.UI.Interactive"/> call —
/// ownership, occlusion and the drag handshake stay exactly where they are.
///
/// <para>Routing is no longer here: a listener names its own edge and its own
/// payload, so the walk dispatches through the typed handler directly and the
/// mode/argument decode this class used to own is gone.</para>
/// </summary>
internal static class InteractionAdapter
{
    /// <summary>Places the reservation at an already-physical rect. The id
    /// string is retained by the root, so a warm frame allocates nothing here.
    /// Space activation stays off: text-button keyboard parity is Enter-only,
    /// exactly as the legacy path.</summary>
    internal static Poser.UI.InteractionResult Reserve(
        string cachedId, Vector2 physicalMin, Vector2 physicalSize, bool disabled)
    {
        ImGui.SetCursorScreenPos(physicalMin);
        return Poser.UI.Interactive.Reserve(cachedId, physicalSize, disabled);
    }
}
