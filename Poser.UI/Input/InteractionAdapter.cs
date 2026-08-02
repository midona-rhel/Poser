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
    /// lands in PendingState and the NEXT build observes it. Whether the
    /// handler takes the element's <see cref="ElementRecord.Arg"/> is the
    /// DECLARATION's business, not a runtime type test: a menu row states one
    /// dispatch mode and both handler shapes follow from it.
    /// </summary>
    internal static void Dispatch(UiRoot root, in ElementRecord record, float value)
    {
        byte mode = record.DispatchMode;
        if (record.EventReducer != 0)
        {
            ScopeTable.Scope? scope = root.Scopes.Find(record.EventScope);
            if (scope is null || root.Arena.GetObject(record.EventReducer) is not Delegate reducer)
                return;
            switch (mode)
            {
                case Reactive.DispatchMode.ClickedWithArg:
                    EventDispatch.Enqueue(root.Scopes, scope, reducer, record.Arg);
                    break;
                case Reactive.DispatchMode.Toggled:
                    EventDispatch.Enqueue(root.Scopes, scope, reducer, record.Arg == 0);
                    break;
                case Reactive.DispatchMode.Drag:
                    EventDispatch.Enqueue(root.Scopes, scope, reducer, value);
                    break;
                default:
                    EventDispatch.Enqueue(root.Scopes, scope, reducer);
                    break;
            }

            return;
        }

        object? behavior = root.Arena.GetObject(record.BehaviorSlot);
        switch (mode)
        {
            // The element carries an INDEX; the item it stands for is resolved
            // by the list's own retained bridge, so the runtime never has to
            // name — or box — the element type.
            case Reactive.DispatchMode.ActivatedItem:
                if (behavior is IItemDispatch items)
                    items.Invoke(record.Arg);
                break;
            case Reactive.DispatchMode.ClickedWithArg:
                if (behavior is Action<int> valuedHandler)
                    valuedHandler(record.Arg);
                break;
            case Reactive.DispatchMode.Toggled:
                if (behavior is Action<bool> toggleHandler)
                    toggleHandler(record.Arg == 0);
                break;
            case Reactive.DispatchMode.Drag:
                if (behavior is Action<float> dragHandler)
                    dragHandler(value);
                break;
            default:
                if (behavior is Action action)
                    action();
                break;
        }
    }
}
