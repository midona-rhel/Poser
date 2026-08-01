using System;
using System.Diagnostics;
using System.Reflection;
using Scope = Poser.UI.Reactive.ScopeTable.Scope;

namespace Poser.UI.Reactive;

/// <summary>
/// Untyped handle the runtime holds for a stateful instance. Public only
/// because <see cref="StatefulComponent{TProps, TState}"/> is public; the
/// members stay internal, so nothing outside the framework can derive here.
/// </summary>
public abstract class StatefulComponentBase
{
    /// <summary>The scope whose Render is executing; UpdateState binds to it.</summary>
    internal static Scope? Ambient;

    internal abstract void ApplyReducer(Scope scope, Delegate reducer);

    internal abstract void ApplyReducer<TValue>(Scope scope, Delegate reducer, TValue value)
        where TValue : unmanaged;

    private protected static int CacheReducer(Scope scope, Delegate reducer)
    {
        RejectCapture(reducer);
        return scope.ReducerSlot(reducer, FrameArena.Require());
    }

    private protected static Scope RequireScope() =>
        Ambient ?? throw new InvalidOperationException(
            "UpdateState may only be called while the owning component is rendering.");

    // A non-capturing lambda is emitted on the shared closure singleton, which
    // declares only static fields; any instance field means a capture.
    [Conditional("DEBUG")]
    private static void RejectCapture(Delegate reducer)
    {
        object? target = reducer.Target;
        if (target is null)
            return;

        FieldInfo[] fields = target.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length != 0)
            throw new InvalidOperationException("capturing lambda in Render; use a static lambda");
    }
}

/// <summary>
/// A component with one typed state record inside its keyed scope. State is
/// stored as <see cref="object"/> so record classes and record structs both
/// work; a record-class update allocates at event time, never on a warm frame.
/// </summary>
public abstract class StatefulComponent<TProps, TState> : StatefulComponentBase
{
    protected abstract TState CreateState(in TProps props);

    protected abstract UiNode Render(in TProps props, in TState state);

    protected UiEvent UpdateState(Func<TState, TState> reducer)
    {
        Scope scope = RequireScope();
        return new UiEvent(scope.Id, CacheReducer(scope, reducer));
    }

    protected UiEvent<TValue> UpdateState<TValue>(Func<TState, TValue, TState> reducer)
        where TValue : unmanaged
    {
        Scope scope = RequireScope();
        return new UiEvent<TValue>(scope.Id, CacheReducer(scope, reducer));
    }

    internal UiNode MountAndRender(Scope scope, in TProps props)
    {
        scope.State ??= CreateState(props);
        if (scope.PendingState is not null)
        {
            scope.State = scope.PendingState;
            scope.PendingState = null;
        }

        Scope? previous = Ambient;
        Ambient = scope;
        try
        {
            return Render(props, (TState)scope.State!);
        }
        finally
        {
            Ambient = previous;
        }
    }

    internal override void ApplyReducer(Scope scope, Delegate reducer) =>
        scope.PendingState = ((Func<TState, TState>)reducer)(Live(scope));

    internal override void ApplyReducer<TValue>(Scope scope, Delegate reducer, TValue value) =>
        scope.PendingState = ((Func<TState, TValue, TState>)reducer)(Live(scope), value);

    // Chained updates in one frame each see the previous reduced result.
    private static TState Live(Scope scope) => (TState)(scope.PendingState ?? scope.State)!;
}
