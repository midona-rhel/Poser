using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Marker interface for all events published through the EventBus.
/// </summary>
public interface IEvent { }

/// <summary>
/// Provides decoupled communication between components via publish/subscribe pattern.
/// </summary>
public class EventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();
    private readonly IPluginLog? _log;

    public EventBus(IPluginLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Subscribe to events of type T.
    /// </summary>
    public void Subscribe<T>(Action<T> handler) where T : IEvent
    {
        lock (_lock)
        {
            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var handlers))
            {
                handlers = new List<Delegate>();
                _handlers[type] = handlers;
            }
            handlers.Add(handler);
        }
    }

    /// <summary>
    /// Unsubscribe from events of type T.
    /// </summary>
    public void Unsubscribe<T>(Action<T> handler) where T : IEvent
    {
        lock (_lock)
        {
            var type = typeof(T);
            if (_handlers.TryGetValue(type, out var handlers))
            {
                handlers.Remove(handler);
            }
        }
    }

    /// <summary>
    /// Publish an event to all subscribers.
    /// </summary>
    public void Publish<T>(T evt) where T : IEvent
    {
        List<Delegate>? handlersCopy;

        lock (_lock)
        {
            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var handlers))
                return;

            // Copy to avoid issues if handlers modify subscriptions
            handlersCopy = new List<Delegate>(handlers);
        }

        foreach (var handler in handlersCopy)
        {
            try
            {
                ((Action<T>)handler)(evt);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not break delivery to other subscribers
                // or crash the publisher (often a framework-tick or hook context).
                _log?.Error(ex, $"EventBus: handler for {typeof(T).Name} threw");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _handlers.Clear();
        }
        GC.SuppressFinalize(this);
    }
}
