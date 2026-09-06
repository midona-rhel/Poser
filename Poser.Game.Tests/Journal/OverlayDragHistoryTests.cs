using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Presentation;
using Poser.Game.Overlays;

namespace Poser.Game.Tests.Journal;

public class OverlayDragHistoryTests
{
    [Fact]
    public void NativeDragReleaseRecordsOneStepAndRestoresDocumentAndNativeState()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var port = new Port();
        using var events = new EventBus(DispatchProxy.Create<IPluginLog, Log>());
        using var service = new OverlayNodeService(port, events,
            DispatchProxy.Create<IPluginLog, Log>(), journal);
        var node = Assert.IsType<OverlayNodeHandle>(service.Create(OverlayNodeKind.Talk));
        Vector2 before = node.Position;
        var after = before + new Vector2(100, 40);
        port.Moved!(port.Token, after);
        Assert.Equal(after, node.Position);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(before, node.Position);
        Assert.Equal(before, port.Applied!.Position);
        history.CommitUndo(step);
        Assert.False(history.CanUndo);
        Assert.True(step.Redo());
        Assert.Equal(after, node.Position);
        Assert.Equal(after, port.Applied!.Position);
    }

    private sealed class Port : IOverlayNodePort
    {
        public object Token { get; } = new();
        public OverlayNodeState? Applied;
        public bool IsAvailable => true;
        public Action<object, Vector2>? Moved { get; set; }
        public object Create(OverlayNodeState state) { Applied = state; return Token; }
        public void Apply(object node, OverlayNodeState state) => Applied = state;
        public void Destroy(object node) { }
        public void Dispose() { }
    }

    public class Log : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
