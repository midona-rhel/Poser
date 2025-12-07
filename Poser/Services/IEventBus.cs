using System;
using Poser.Core;

namespace Poser.Services;

/// <summary>
/// Provides decoupled communication between components via publish/subscribe pattern.
/// </summary>
public interface IEventBus : IDisposable
{
    /// <summary>
    /// Subscribe to events of type T.
    /// </summary>
    void Subscribe<T>(Action<T> handler) where T : IEvent;

    /// <summary>
    /// Unsubscribe from events of type T.
    /// </summary>
    void Unsubscribe<T>(Action<T> handler) where T : IEvent;

    /// <summary>
    /// Publish an event to all subscribers.
    /// </summary>
    void Publish<T>(T evt) where T : IEvent;
}
