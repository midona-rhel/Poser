using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// The single seam between the retained tree and the imperative input
/// kernel. The tree already knows where an element sits, so reservation is
/// a cursor placement plus the ordinary <see cref="Poser.UI.Interactive"/>
/// call — ownership, occlusion, and the drag handshake stay exactly where
/// they are. Activation is dispatched AFTER the walk so a handler can never
/// mutate state that the same frame is still painting.
/// </summary>
internal static class InteractionAdapter
{
    /// <summary>Places the reservation at an already-physical rect. The
    /// id string is retained by the root, so a warm frame allocates
    /// nothing here. Space activation stays off: text-button keyboard
    /// parity is Enter-only, exactly as the legacy path.</summary>
    internal static Poser.UI.InteractionResult Reserve(
        string cachedId, Vector2 physicalMin, Vector2 physicalSize, bool disabled)
    {
        ImGui.SetCursorScreenPos(physicalMin);
        return Poser.UI.Interactive.Reserve(cachedId, physicalSize, disabled);
    }

    /// <summary>
    /// Routes one activation. A plain <see cref="Action"/> runs inline; a
    /// component event is a (scope, reducer-slot) pair, so the reducer is
    /// looked up by slot and queued against its owning scope — the result
    /// lands in PendingState and the NEXT build observes it.
    /// </summary>
    internal static void Dispatch(UiRoot root, in ElementRecord record)
    {
        if (record.EventReducer != 0)
        {
            ScopeTable.Scope? scope = root.Scopes.Find(record.EventScope);
            if (scope is not null && root.Arena.GetObject(record.EventReducer) is Delegate reducer)
                EventDispatch.Enqueue(root.Scopes, scope, reducer);
            return;
        }

        if (root.Arena.GetObject(record.BehaviorSlot) is Action action)
            action();
    }
}
