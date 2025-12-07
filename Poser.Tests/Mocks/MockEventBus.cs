using System;
using System.Collections.Generic;
using Poser.Core;
using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly List<IEvent> _publishedEvents = new();

    public IReadOnlyList<IEvent> PublishedEvents => _publishedEvents.AsReadOnly();

    public void Subscribe<T>(Action<T> handler) where T : IEvent
    {
        var type = typeof(T);
        if (!_handlers.ContainsKey(type))
        {
            _handlers[type] = new List<Delegate>();
        }
        _handlers[type].Add(handler);
    }

    public void Unsubscribe<T>(Action<T> handler) where T : IEvent
    {
        var type = typeof(T);
        if (_handlers.ContainsKey(type))
        {
            _handlers[type].Remove(handler);
        }
    }

    public void Publish<T>(T evt) where T : IEvent
    {
        _publishedEvents.Add(evt);
        var type = typeof(T);
        if (_handlers.TryGetValue(type, out var handlers))
        {
            foreach (var handler in handlers)
            {
                ((Action<T>)handler)(evt);
            }
        }
    }

    public void ClearPublishedEvents()
    {
        _publishedEvents.Clear();
    }

    public void Dispose()
    {
        _handlers.Clear();
        _publishedEvents.Clear();
    }
}
