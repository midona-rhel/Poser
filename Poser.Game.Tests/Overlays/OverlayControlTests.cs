using System.Numerics;
using System.Reflection;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Overlays;
using Poser.Services;

namespace Poser.Game.Tests.Overlays;

public sealed class OverlayControlTests
{
    [Fact]
    public void Refused_or_stale_overlay_write_returns_failure_and_appends_nothing()
    {
        var id = OverlayId.New();
        var current = id;
        var node = new Node();
        var history = new TransformHistory();
        var earlier = new JournalStep("Earlier edit", () => true, () => true);
        history.Append(earlier);
        history.CommitUndo(earlier);
        var bindings = Stub<IEntityBindings>((m, a) => m.Name switch
        {
            "Resolve" => (OverlayId)a![0]! == current
                ? new BindingResult<IOverlayNode>(BindingStatus.Success, node)
                : new BindingResult<IOverlayNode>(BindingStatus.StaleTarget),
            _ => throw new InvalidOperationException(m.Name),
        });
        var control = new OverlayControl(bindings, new ValueJournal(history));

        var refused = control.EditCollider(id, c => c with { Enabled = false });
        current = id.NextGeneration();
        var stale = control.Set(id, OverlayProperties.Text, "Hello");

        Assert.Equal((false, "The collider is no longer available."), (refused.Success, refused.Detail));
        Assert.Equal((false, "The overlay is no longer available."), (stale.Success, stale.Detail));
        Assert.Equal(string.Empty, node.Text);
        Assert.False(history.CanUndo);
        Assert.Same(earlier, history.PeekRedo());
    }

    private sealed class Node : IOverlayNode
    {
        public int Id => 1;
        public OverlayNodeKind Kind => State.Kind;
        public bool IsValid => true;
        public OverlayNodeState State { get; set; } = new();
        public string Name { get => State.Name; set => State = State with { Name = value }; }
        public Vector2 Position { get => State.Position; set => State = State with { Position = value }; }
        public float Scale { get => State.Scale; set => State = State with { Scale = value }; }
        public float Alpha { get => State.Alpha; set => State = State with { Alpha = value }; }
        public bool Visible { get => State.Visible; set => State = State with { Visible = value }; }
        public bool Draggable { get => State.Draggable; set => State = State with { Draggable = value }; }
        public string Text { get => State.Text; set => State = State with { Text = value }; }
        public string Speaker { get => State.Speaker; set => State = State with { Speaker = value }; }
        public uint FontSize { get => State.FontSize; set => State = State with { FontSize = value }; }
        public TalkBackground TalkBackground { get => State.TalkBackground; set => State = State with { TalkBackground = value }; }
        public TalkCursor TalkCursor { get => State.TalkCursor; set => State = State with { TalkCursor = value }; }
        public BalloonChannel BalloonChannel { get => State.BalloonChannel; set => State = State with { BalloonChannel = value }; }
        public BalloonGradient BalloonGradient { get => State.BalloonGradient; set => State = State with { BalloonGradient = value }; }
        public bool ArrowVisible { get => State.ArrowVisible; set => State = State with { ArrowVisible = value }; }
        public float ArrowX { get => State.ArrowX; set => State = State with { ArrowX = value }; }
        public StatusKind StatusKind { get => State.StatusKind; set => State = State with { StatusKind = value }; }
        public uint StatusIconId { get => State.StatusIconId; set => State = State with { StatusIconId = value }; }
        public void Destroy() { }
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
