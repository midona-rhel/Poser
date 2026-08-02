using System;
using Poser.UI.Reactive;
using Scope = Poser.UI.Reactive.ScopeTable.Scope;

namespace Poser.UI;

/// <summary>
/// Frame token binding a control's callback to one scope's cached reducer.
/// Slot 0 is the reserved arena object slot, so a default token is "no handler".
/// Under DEBUG the token also carries the arena and frame that minted it, on
/// the same terms as <see cref="UiNode"/>: a slot index outlives nothing.
/// </summary>
public readonly struct UiEvent
{
    internal readonly int ScopeId;
    internal readonly int ReducerSlot;
#if DEBUG
    internal readonly int Frame;
    internal readonly int Arena;
#endif

    internal UiEvent(int scopeId, int reducerSlot, int frame, int arena)
    {
        ScopeId = scopeId;
        ReducerSlot = reducerSlot;
#if DEBUG
        Frame = frame;
        Arena = arena;
#else
        _ = frame;
        _ = arena;
#endif
    }

    internal bool IsNone => ReducerSlot == 0;
}

/// <summary>
/// As <see cref="UiEvent"/>, but the control supplies the value at dispatch
/// time; the token never stores it. The payload is unconstrained — dispatch
/// is generic all the way down, so a reference payload boxes nothing either.
/// </summary>
public readonly struct UiEvent<TValue>
{
    internal readonly int ScopeId;
    internal readonly int ReducerSlot;
#if DEBUG
    internal readonly int Frame;
    internal readonly int Arena;
#endif

    internal UiEvent(int scopeId, int reducerSlot, int frame, int arena)
    {
        ScopeId = scopeId;
        ReducerSlot = reducerSlot;
#if DEBUG
        Frame = frame;
        Arena = arena;
#else
        _ = frame;
        _ = arena;
#endif
    }

    internal bool IsNone => ReducerSlot == 0;
}

/// <summary>
/// Queued state updates. Reducers run against the scope's current (already
/// queued) state and park their result in PendingState; the root applies it
/// before the next build, so a frame observes one consistent state.
/// </summary>
internal static class EventDispatch
{
    internal static void Enqueue(ScopeTable scopes, Scope scope, Delegate reducer) =>
        Component(scopes, scope).ApplyReducer(scope, reducer);

    internal static void Enqueue<TValue>(ScopeTable scopes, Scope scope, Delegate reducer, TValue value) =>
        Component(scopes, scope).ApplyReducer(scope, reducer, value);

    private static StatefulComponentBase Component(ScopeTable scopes, Scope scope)
    {
        _ = scopes;
        return scope.Instance as StatefulComponentBase
            ?? throw new InvalidOperationException(
                $"Scope {scope.Id} ({scope.ComponentType.Name}) has no stateful component instance to reduce.");
    }
}
