using System;
using System.Diagnostics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// One handler, either world. A control's prop is a <see cref="UiHandler"/>
/// and nothing else: a plain stable delegate and a component
/// <c>UpdateState</c> token both convert implicitly, so no control carries an
/// <c>Action</c>/<c>UiEvent</c> overload pair and dispatch is implemented
/// once on the base.
/// </summary>
public readonly struct UiHandler
{
    private readonly Action? _action;
    private readonly UiEvent _event;

    private UiHandler(Action? action, UiEvent token)
    {
        _action = action;
        _event = token;
    }

    public static implicit operator UiHandler(Action? action) =>
        new(action, default);

    public static implicit operator UiHandler(UiEvent token) =>
        new(null, token);

    internal bool IsNone => _action is null && _event.IsNone;

    [Conditional("DEBUG")]
    internal void Validate(FrameArena arena) => arena.ValidateEvent(in _event);

    internal void Invoke(UiRoot root)
    {
        if (_action is { } action)
        {
            action();
            return;
        }

        EventDispatch.Dispatch(root, _event);
    }
}

/// <summary>
/// As <see cref="UiHandler"/>, with the value the base resolved at dispatch
/// time — the toggled flag, the dragged value, the picked index. The payload
/// never rides the declaration, so binding a handler boxes nothing.
/// </summary>
public readonly struct UiHandler<TValue>
{
    private readonly Action<TValue>? _action;
    private readonly UiEvent<TValue> _event;

    private UiHandler(Action<TValue>? action, UiEvent<TValue> token)
    {
        _action = action;
        _event = token;
    }

    public static implicit operator UiHandler<TValue>(Action<TValue>? action) =>
        new(action, default);

    public static implicit operator UiHandler<TValue>(UiEvent<TValue> token) =>
        new(null, token);

    internal bool IsNone => _action is null && _event.IsNone;

    [Conditional("DEBUG")]
    internal void Validate(FrameArena arena) => arena.ValidateEvent(in _event);

    internal void Invoke(UiRoot root, TValue value)
    {
        if (_action is { } action)
        {
            action(value);
            return;
        }

        EventDispatch.Dispatch(root, _event, value);
    }
}
