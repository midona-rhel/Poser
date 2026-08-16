using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Presentation;
using Poser.Game.Overlays;
using Poser.Services;

namespace Poser.Game.Tests.Overlays;

public sealed class OverlayNodeServiceTests
{
    [Fact]
    public void Create_write_drag_and_destroy_preserves_identity_and_state()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk);
        Assert.NotNull(handle);
        var node = Assert.Single(world.Port.Live);

        handle.Text = "Hello";
        world.Port.Drag(node, new Vector2(320, 240));
        handle.Draggable = false;

        Assert.Equal("Hello", handle.Text);
        Assert.Equal(new Vector2(320, 240), handle.Position);
        Assert.Equal(new Vector2(320, 240), node.State.Position);
        Assert.Equal(1, world.Events.ListChanges);

        world.Service.Destroy(handle);
        world.Service.Destroy(handle);

        Assert.False(handle.IsValid);
        Assert.Empty(world.Service.Nodes);
        Assert.Empty(world.Port.Live);
        Assert.Equal(1, world.Port.Destroys);
    }

    [Fact]
    public void Refused_create_does_not_attach_or_emit_a_list_change()
    {
        var world = new World { Port = { RefuseCreate = true } };

        Assert.Null(world.Service.Create(OverlayNodeKind.Talk));

        Assert.Empty(world.Service.Nodes);
        Assert.Empty(world.Port.Live);
        Assert.Equal(0, world.Events.ListChanges);
    }

    [Fact]
    public void Leaving_gpose_destroys_all_nodes_once()
    {
        var world = new World();
        world.Service.Create(OverlayNodeKind.Talk);
        world.Service.Create(OverlayNodeKind.Status);

        world.Events.Publish(new GPoseStateChangedEvent(false));
        world.Events.Publish(new GPoseStateChangedEvent(false));

        Assert.Empty(world.Service.Nodes);
        Assert.Empty(world.Port.Live);
        Assert.Equal(2, world.Port.Destroys);
    }

    [Fact]
    public void Dispose_destroys_nodes_before_the_port_and_is_idempotent()
    {
        var world = new World();
        world.Service.Create(OverlayNodeKind.Talk);
        world.Service.Create(OverlayNodeKind.Balloon);

        world.Service.Dispose();
        world.Service.Dispose();

        Assert.Empty(world.Port.Live);
        Assert.Equal(2, world.Port.Destroys);
        Assert.Equal(2, world.Port.DestroysBeforeDispose);
        Assert.True(world.Port.Disposed);
    }

    [Fact]
    public void Port_destroy_failure_still_removes_the_handle_without_retry()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk);
        Assert.NotNull(handle);
        world.Port.ThrowOnDestroy = true;

        world.Service.Destroy(handle);
        world.Service.Destroy(handle);

        Assert.False(handle.IsValid);
        Assert.Empty(world.Service.Nodes);
        Assert.Empty(world.Port.Live);
        Assert.Equal(1, world.Port.Destroys);
    }

    [Fact]
    public void Disposed_service_refuses_new_nodes_and_keeps_document_local_state()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk);
        Assert.NotNull(handle);
        world.Service.Dispose();

        Assert.Null(world.Service.Create(OverlayNodeKind.Talk));
        Assert.False(world.Service.IsAvailable);

        handle.Text = "after unload";
        Assert.Equal("after unload", handle.Text);
        Assert.Empty(world.Port.Live);
    }

    private sealed class World
    {
        public FakePort Port { get; init; } = new();
        public FakeEventBus Events { get; } = new();
        public OverlayNodeService Service { get; }

        public World() => Service = new OverlayNodeService(
            Port, Events, DispatchProxy.Create<IPluginLog, SilentLog>());
    }

    private sealed class FakePort : IOverlayNodePort
    {
        private readonly List<FakeNode> _live = new();

        public bool RefuseCreate { get; set; }
        public bool ThrowOnDestroy { get; set; }
        public bool Disposed { get; private set; }
        public int Destroys { get; private set; }
        public int DestroysBeforeDispose { get; private set; }
        public IReadOnlyList<FakeNode> Live => _live;
        public bool IsAvailable => !Disposed && !RefuseCreate;
        public Action<object, Vector2>? Moved { get; set; }

        public object? Create(OverlayNodeState state)
        {
            if (!IsAvailable)
                return null;
            var node = new FakeNode { State = state };
            _live.Add(node);
            return node;
        }

        public void Apply(object node, OverlayNodeState state)
        {
            if (node is FakeNode fake && _live.Contains(fake))
                fake.State = state;
        }

        public void Destroy(object node)
        {
            if (node is not FakeNode fake || !_live.Remove(fake))
                return;
            Destroys++;
            if (!Disposed)
                DestroysBeforeDispose = Destroys;
            if (ThrowOnDestroy)
                throw new InvalidOperationException("destroy refused");
        }

        public void Dispose()
        {
            Disposed = true;
            DestroysBeforeDispose = Destroys;
            Destroys += _live.Count;
            _live.Clear();
        }

        public void Drag(FakeNode node, Vector2 position)
        {
            node.State = node.State with { Position = position };
            Moved?.Invoke(node, position);
        }
    }

    private sealed class FakeNode
    {
        public OverlayNodeState State { get; set; } = new();
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        public int ListChanges { get; private set; }

        public void Dispose() { }

        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
        }

        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        public void Publish<T>(T evt) where T : IEvent
        {
            if (evt is OverlayNodeListChangedEvent)
                ListChanges++;
            if (_handlers.TryGetValue(typeof(T), out var list))
                foreach (var handler in list.ToArray())
                    ((Action<T>)handler)(evt);
        }
    }

    private class SilentLog : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod, object?[]? args) => null;
    }
}
