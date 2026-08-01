using System;
using Scope = Poser.UI.Reactive.ScopeTable.Scope;

namespace Poser.UI.Reactive;

/// <summary>
/// Frame token binding a control's callback to one scope's cached reducer.
/// Slot 0 is the reserved arena object slot, so a default token is "no handler".
/// </summary>
public readonly struct UiEvent
{
    internal readonly int ScopeId;
    internal readonly int ReducerSlot;

    internal UiEvent(int scopeId, int reducerSlot)
    {
        ScopeId = scopeId;
        ReducerSlot = reducerSlot;
    }

    internal bool IsNone => ReducerSlot == 0;
}

/// <summary>
/// As <see cref="UiEvent"/>, but the control supplies the value at dispatch
/// time; the token never stores it.
/// </summary>
public readonly struct UiEvent<TValue>
    where TValue : unmanaged
{
    internal readonly int ScopeId;
    internal readonly int ReducerSlot;

    internal UiEvent(int scopeId, int reducerSlot)
    {
        ScopeId = scopeId;
        ReducerSlot = reducerSlot;
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

    internal static void Enqueue<TValue>(ScopeTable scopes, Scope scope, Delegate reducer, TValue value)
        where TValue : unmanaged =>
        Component(scopes, scope).ApplyReducer(scope, reducer, value);

    private static StatefulComponentBase Component(ScopeTable scopes, Scope scope)
    {
        _ = scopes;
        return scope.Instance as StatefulComponentBase
            ?? throw new InvalidOperationException(
                $"Scope {scope.Id} ({scope.ComponentType.Name}) has no stateful component instance to reduce.");
    }
}
