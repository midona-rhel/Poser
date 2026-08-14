using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Presentation;
using Poser.Game.Overlays;
using Poser.Services;

namespace Poser.Game.Tests.Overlays;

/// <summary>
/// The overlay-node service's contract, and above all its TEARDOWN contract.
/// A node the service forgets without freeing is a native UI subtree the game
/// keeps drawing after the plugin that owns it has gone, so every edge that
/// can end a node's life is proven here: an explicit destroy, a scene clear,
/// GPose exit, and plugin unload — each of them idempotent, and any pair of
/// them safe in either order.
/// </summary>
public sealed class OverlayNodeServiceTests
{
    [Fact]
    public void Creating_a_node_attaches_exactly_one_and_lists_it()
    {
        var world = new World();

        var handle = world.Service.Create(OverlayNodeKind.Talk);

        Assert.NotNull(handle);
        Assert.True(handle!.IsValid);
        Assert.Single(world.Port.Live);
        Assert.Same(handle, Assert.Single(world.Service.Nodes));
        Assert.Equal(1, world.Events.ListChanges);
    }

    [Fact]
    public void A_created_node_carries_its_kinds_own_defaults()
    {
        var world = new World();

        var talk = world.Service.Create(OverlayNodeKind.Talk)!;
        var balloon = world.Service.Create(OverlayNodeKind.Balloon)!;
        var status = world.Service.Create(OverlayNodeKind.Status)!;

        Assert.Equal("Speaker", talk.Speaker);
        Assert.Equal(TalkCursor.Pin, talk.TalkCursor);
        Assert.Equal(BalloonChannel.Say, balloon.BalloonChannel);
        Assert.True(balloon.ArrowVisible);
        Assert.Equal(StatusKind.Buff, status.StatusKind);
        Assert.Equal(
            OverlayNodeService.DefaultStatusIconId, status.StatusIconId);
    }

    [Fact]
    public void Nodes_are_named_per_kind_and_a_name_is_never_reused()
    {
        var world = new World();

        var first = world.Service.Create(OverlayNodeKind.Talk)!;
        var balloon = world.Service.Create(OverlayNodeKind.Balloon)!;
        var second = world.Service.Create(OverlayNodeKind.Talk)!;

        Assert.Equal("Dialog 1", first.Name);
        Assert.Equal("Balloon 1", balloon.Name);
        Assert.Equal("Dialog 2", second.Name);

        // The name a destroyed node held is not handed back: an undo that
        // restores it must not collide with something spawned since.
        world.Service.Destroy(second);
        Assert.Equal("Dialog 2", world.Service.Create(OverlayNodeKind.Talk)!.Name);
    }

    [Fact]
    public void A_refused_create_leaves_nothing_attached()
    {
        var world = new World();
        world.Port.RefuseCreate = true;

        Assert.Null(world.Service.Create(OverlayNodeKind.Talk));
        Assert.Empty(world.Port.Live);
        Assert.Empty(world.Service.Nodes);
        Assert.Equal(0, world.Events.ListChanges);
    }

    [Fact]
    public void Writing_a_field_restates_the_whole_node()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk)!;

        handle.Text = "Hello.";
        handle.Position = new Vector2(120f, 240f);

        var node = Assert.Single(world.Port.Live);
        Assert.Equal("Hello.", node.State.Text);
        Assert.Equal(new Vector2(120f, 240f), node.State.Position);
        Assert.Equal("Hello.", handle.Text);
    }

    [Fact]
    public void A_write_out_of_range_is_bounded_before_it_reaches_the_game()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Balloon)!;

        handle.Scale = 500f;
        handle.Alpha = float.NaN;
        handle.ArrowX = -40f;
        handle.Text = new string('x', OverlayNodeLimits.MaxTextCharacters + 50);

        var node = Assert.Single(world.Port.Live);
        Assert.Equal(OverlayNodeLimits.MaxScale, node.State.Scale);
        Assert.Equal(1f, node.State.Alpha);
        Assert.Equal(OverlayNodeLimits.MinArrowX, node.State.ArrowX);
        Assert.Equal(
            OverlayNodeLimits.MaxTextCharacters, node.State.Text.Length);
    }

    // ── teardown ─────────────────────────────────────────────────────────

    [Fact]
    public void Destroying_a_node_frees_it_exactly_once()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk)!;

        world.Service.Destroy(handle);
        world.Service.Destroy(handle);

        Assert.False(handle.IsValid);
        Assert.Empty(world.Port.Live);
        Assert.Equal(1, world.Port.Destroys);
    }

    [Fact]
    public void Clearing_the_scene_frees_every_node()
    {
        var world = new World();
        world.Service.Create(OverlayNodeKind.Talk);
        world.Service.Create(OverlayNodeKind.Balloon);
        world.Service.Create(OverlayNodeKind.Status);

        world.Service.DestroyAll();

        Assert.Empty(world.Service.Nodes);
        Assert.Empty(world.Port.Live);
        Assert.Equal(3, world.Port.Destroys);
    }

    [Fact]
    public void Leaving_GPose_frees_every_node()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk)!;

        world.Events.Publish(new GPoseStateChangedEvent(false));

        Assert.False(handle.IsValid);
        Assert.Empty(world.Port.Live);
    }

    [Fact]
    public void Entering_GPose_frees_nothing()
    {
        var world = new World();
        world.Service.Create(OverlayNodeKind.Talk);

        world.Events.Publish(new GPoseStateChangedEvent(true));

        Assert.Single(world.Port.Live);
    }

    [Fact]
    public void Unloading_frees_every_node_and_then_the_port()
    {
        var world = new World();
        world.Service.Create(OverlayNodeKind.Talk);
        world.Service.Create(OverlayNodeKind.Status);

        world.Service.Dispose();

        Assert.Empty(world.Port.Live);
        Assert.Equal(2, world.Port.Destroys);
        Assert.True(world.Port.Disposed);
        // The port is freed AFTER the nodes, never before: a port disposed
        // first would be handed tokens it no longer owns.
        Assert.Equal(2, world.Port.DestroysBeforeDispose);
    }

    [Fact]
    public void Unloading_after_GPose_exit_is_still_correct()
    {
        var world = new World();
        world.Service.Create(OverlayNodeKind.Talk);
        world.Events.Publish(new GPoseStateChangedEvent(false));

        world.Service.Dispose();

        Assert.Empty(world.Port.Live);
        Assert.Equal(1, world.Port.Destroys);
        Assert.True(world.Port.Disposed);
    }

    [Fact]
    public void A_disposed_service_neither_creates_nor_writes()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk)!;
        world.Service.Dispose();

        Assert.Null(world.Service.Create(OverlayNodeKind.Talk));
        Assert.False(world.Service.IsAvailable);

        // The handle's own document still answers, but nothing reaches the
        // game — the node behind it is gone.
        handle.Text = "after";
        Assert.Equal("after", handle.Text);
        Assert.Empty(world.Port.Live);
    }

    [Fact]
    public void A_port_that_throws_on_destroy_still_loses_the_node()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk)!;
        world.Port.ThrowOnDestroy = true;

        world.Service.Destroy(handle);

        // The token is never handed back: a half-freed node must not be freed
        // a second time.
        Assert.False(handle.IsValid);
        Assert.Empty(world.Service.Nodes);
    }

    [Fact]
    public void A_restored_document_comes_back_whole()
    {
        var world = new World();
        var original = world.Service.Create(OverlayNodeKind.Status)!;
        original.Text = "Staged";
        original.StatusKind = StatusKind.Falloff;
        original.Position = new Vector2(64f, 96f);
        var document = original.State;
        world.Service.Destroy(original);

        var restored = world.Service.Create(document)!;

        Assert.Equal(document, restored.State);
        Assert.Equal("Staged", restored.Text);
        Assert.Equal(StatusKind.Falloff, restored.StatusKind);
    }

    [Fact]
    public void A_dragged_node_keeps_where_the_pointer_left_it()
    {
        var world = new World();
        var handle = world.Service.Create(OverlayNodeKind.Talk)!;
        var node = Assert.Single(world.Port.Live);

        world.Port.Drag(node, new Vector2(320f, 240f));

        // The document caught up without a write: the node was never re-stated.
        Assert.Equal(new Vector2(320f, 240f), handle.Position);

        // The toggle the user reaches for after dragging — and the bug it used
        // to carry: this write re-stated the position the node had BEFORE the
        // drag, and the node snapped home.
        handle.Draggable = false;

        Assert.Equal(new Vector2(320f, 240f), handle.Position);
        Assert.Equal(new Vector2(320f, 240f), node.State.Position);
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    private sealed class World
    {
        public FakePort Port { get; } = new();
        public FakeEventBus Events { get; } = new();
        public OverlayNodeService Service { get; }

        public World() =>
            Service = new OverlayNodeService(
                Port, Events, DispatchProxy.Create<IPluginLog, SilentLog>());
    }

    /// <summary>
    /// The node port, faithfully: a token per node, one create per token, a
    /// destroy that is inert the second time, and a dispose that frees
    /// whatever is left. Everything the real port promises, with the game
    /// replaced by a list.
    /// </summary>
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

        /// <summary>The pointer's own drag, as the game performs it: the node
        /// moves, the port says so, and nothing is written back down.</summary>
        public void Drag(FakeNode node, Vector2 to)
        {
            node.State = node.State with { Position = to };
            Moved?.Invoke(node, to);
        }

        public object? Create(OverlayNodeState state)
        {
            if (Disposed || RefuseCreate)
                return null;
            var node = new FakeNode { State = state };
            _live.Add(node);
            return node;
        }

        public void Apply(object node, OverlayNodeState state)
        {
            var fake = (FakeNode)node;
            if (!_live.Contains(fake))
                return;
            fake.State = state;
        }

        public void Destroy(object node)
        {
            var fake = (FakeNode)node;
            if (!_live.Remove(fake))
                return;
            Destroys++;
            if (!Disposed)
                DestroysBeforeDispose = Destroys;
            if (ThrowOnDestroy)
                throw new InvalidOperationException("the game refused it");
        }

        public void Dispose()
        {
            Disposed = true;
            for (int i = 0; i < _live.Count; i++)
                Destroys++;
            _live.Clear();
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
            MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType is { IsValueType: true } type &&
                type != typeof(void))
                return Activator.CreateInstance(type);
            return null;
        }
    }
}
