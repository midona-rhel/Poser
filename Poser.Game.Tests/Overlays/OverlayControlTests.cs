using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Journal;
using Poser.Game.Overlays;
using Poser.Services;

namespace Poser.Game.Tests.Overlays;

public sealed class OverlayControlTests
{
    [Fact]
    public void Position_drag_keeps_detached_reading_and_one_reversible_history_step()
    {
        using var f = new Fixture();
        var before = f.Control.Read(f.Id)!;
        for (int i = 1; i <= 3; i++)
        {
            f.Journal.BeginEdit("pad");
            Assert.True(f.Control.SetPosition(f.Id, new(100 + i, 80 + i)).Success);
            f.Journal.EndEdit();
        }
        Assert.False(f.History.CanUndo);
        f.Control.Seal();
        Assert.Equal(Vector2.Zero, before.State.Position);
        Assert.Equal(new Vector2(103, 83), f.Node.Position);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(before.State.Position, f.Node.Position);
        f.History.CommitUndo(step);
        Assert.False(f.History.CanUndo);
        Assert.True(step.Redo());
        Assert.Equal(new Vector2(103, 83), f.Node.Position);
    }

    [Fact]
    public void Collider_property_edit_preserves_newer_state_and_has_undo()
    {
        using var f = new Fixture(OverlayNodeKind.Collider);
        var before = f.Control.Read(f.Id)!;
        var moved = before.State.Collider!.Transform with { Position = new(3, 4, 5) };
        f.Node.State = f.Node.State with
        {
            Collider = f.Node.State.Collider! with { Transform = moved, Locked = true },
        };
        Assert.True(f.Control.SetCollisionEnabled(f.Id, false).Success);
        Assert.Equal(moved, f.Node.State.Collider!.Transform);
        Assert.True(f.Node.State.Collider.Locked);
        Assert.True(before.State.Collider.Enabled);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.True(f.Node.State.Collider.Enabled);
        Assert.Equal(moved, f.Node.State.Collider.Transform);
        Assert.True(step.Redo());
        Assert.False(f.Node.State.Collider.Enabled);
    }

    [Fact]
    public void Replaced_destroyed_and_off_thread_targets_never_receive_delayed_edits()
    {
        using var f = new Fixture();
        f.CurrentId = new(f.Id.LogicalId, f.Id.Generation + 1);
        Assert.Null(f.Control.Read(f.Id));
        Assert.False(f.Control.SetStatusIconId(f.Id, 123).Success);
        Assert.Equal(0u, f.Node.StatusIconId);
        Assert.False(f.History.CanUndo);
        f.OnThread = false;
        int reads = f.BindingReads;
        Assert.False(f.Control.SetText(f.CurrentId, "No").Success);
        Assert.Equal(reads, f.BindingReads);
        f.OnThread = true;
        f.Node.Destroy();
        Assert.False(f.Control.SetText(f.CurrentId, "No").Success);
        Assert.False(f.History.CanUndo);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly OverlayId Id = new(Guid.NewGuid(), 0);
        public OverlayId CurrentId;
        public bool OnThread = true;
        public int BindingReads;
        public readonly TransformHistory History = new();
        public readonly ValueJournal Journal;
        public readonly IOverlayNode Node;
        public readonly OverlayControl Control;
        private readonly EventBus _events;
        private readonly OverlayNodeService _service;

        public Fixture(OverlayNodeKind kind = OverlayNodeKind.Talk)
        {
            CurrentId = Id;
            Journal = new(History);
            var log = Stub<IPluginLog>((_, _) => null);
            _events = new(log);
            _service = new(new Port(), _events, log, new Lazy<ValueJournal>(() => Journal));
            Node = _service.Create(kind)!;
            Node.Position = Vector2.Zero;
            var bindings = Stub<IEntityBindings>((m, _) =>
            {
                BindingReads++;
                return m.Name switch
                {
                    "Resolve" => new BindingResult<IOverlayNode>(BindingStatus.Success, Node),
                    "GetOverlayId" => CurrentId,
                    _ => throw new InvalidOperationException(m.Name),
                };
            });
            Control = new(bindings, Stub<IFramework>((_, _) => OnThread), new OverlaySession(Journal));
        }
        public void Dispose() { _service.Dispose(); _events.Dispose(); }
    }

    private sealed class Port : IOverlayNodePort
    {
        public bool IsAvailable => true;
        public Action<object, Vector2>? Moved { get; set; }
        public object Create(OverlayNodeState state) => new();
        public void Apply(object node, OverlayNodeState state) { }
        public void Destroy(object node) { }
        public void Dispose() { }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Call = call;
        return proxy;
    }
    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
