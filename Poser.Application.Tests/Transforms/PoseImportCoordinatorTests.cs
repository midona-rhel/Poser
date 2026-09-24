using System.Reflection;
using Poser.Application.Animation;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Transforms;
using Poser.Files;

namespace Poser.Application.Tests.Transforms;

public sealed class PoseImportCoordinatorTests
{
    private static readonly ActorId Actor = new(Guid.NewGuid(), 0);

    [Fact]
    public void ImportHoldsPauseUntilCompletionAndRestoresPreviousSpeed()
    {
        var f = new Fixture();
        f.Animation.SetSpeed(Actor, .5f);
        var options = new PoseImportOptions { AsExpression = true };
        Assert.True(f.Coordinator.Begin(Actor, new Plan(), options, "Import").Success);
        Assert.True(f.Animation.IsPaused(Actor));
        Assert.Empty(f.Runtime.Applied);
        options.AsExpression = false;
        f.Runtime.Tick(4);
        Assert.Equal(["rewind", "apply"], f.Events.TakeLast(2));
        Assert.True(Assert.Single(f.Runtime.Applied).Expression);
        Assert.True(f.Animation.IsPaused(Actor));
        f.Runtime.Finish(true);
        Assert.True(f.Animation.IsPaused(Actor));
        f.Runtime.Tick(2);
        Assert.Equal(.5f, f.Animation.OverridesFor(Actor).OverallSpeed);
        Assert.False(f.Coordinator.IsImportBusy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreezePersistsOnlyAfterSuccessfulImport(bool success)
    {
        var f = new Fixture();
        f.Coordinator.Begin(Actor, new Plan(), new() { FreezeOnImport = true }, "Import");
        f.Runtime.Tick(4);
        f.Runtime.Finish(success);
        f.Runtime.Tick(2);
        Assert.Equal(success, f.Animation.IsPaused(Actor));
    }

    [Fact]
    public void UserPausedActorStaysPausedWithoutFreezeOnImport()
    {
        var f = new Fixture();
        f.Animation.Pause(Actor);
        f.Coordinator.Begin(Actor, new Plan(), new(), "Import");
        f.Runtime.Tick(4);
        f.Runtime.Finish(true);
        f.Runtime.Tick(2);
        Assert.True(f.Animation.IsPaused(Actor));
    }

    [Fact]
    public void SupersededArmCannotApplyOrRestoreItsSuccessorsPause()
    {
        var f = new Fixture();
        f.Coordinator.Begin(Actor, new Plan(), new(), "Old");
        f.Coordinator.Begin(Actor, new Plan(), new(), "New");
        f.Runtime.Tick(2); // Old cancellation's deferred restore.
        Assert.True(f.Animation.IsPaused(Actor));
        f.Runtime.Tick(4); // Old settle callback must do nothing.
        Assert.Single(f.Runtime.Applied);
        f.Runtime.Finish(true);
        f.Runtime.Tick(2);
        Assert.False(f.Animation.IsPaused(Actor));
    }

    [Fact]
    public void CompletedImportsDeferredRestoreCannotResumeTheNextImport()
    {
        var f = new Fixture();
        f.Coordinator.Begin(Actor, new Plan(), new(), "Old");
        f.Runtime.Tick(4);
        f.Runtime.Finish(true); // Speed restore remains queued for two ticks.
        f.Coordinator.Begin(Actor, new Plan(), new(), "New");
        f.Runtime.Tick(2);
        Assert.True(f.Animation.IsPaused(Actor));
        f.Runtime.Tick(4);
        f.Runtime.Finish(true);
        f.Runtime.Tick(2);
        Assert.False(f.Animation.IsPaused(Actor));
    }

    private sealed class Plan : IPreparedPoseImport
    {
        public bool IsEmpty => false;
        public int FileBoneCount => 1;
    }

    private sealed class Fixture
    {
        public readonly List<string> Events = [];
        public readonly Runtime Runtime;
        public readonly AnimationSession Animation;
        public readonly PoseImportCoordinator Coordinator;
        public Fixture()
        {
            var port = DispatchProxy.Create<IAnimationRuntimePort, AnimationPort>();
            ((AnimationPort)(object)port).Events = Events;
            Animation = new(port);
            Runtime = new(Events);
            Coordinator = new(Runtime, Animation);
        }
    }

    // Only the animation mechanisms used by the import workflow are permitted.
    public class AnimationPort : DispatchProxy
    {
        public List<string> Events = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "IsSupported") return true;
            if (method.Name == "RewindPausedControls") Events.Add("rewind");
            if (method.Name is "SetOverallSpeed" or "ClearOverallSpeed" or "RewindPausedControls")
                return AnimationPortResult.Ok();
            throw new InvalidOperationException("Unexpected animation call: " + method.Name);
        }
    }

    private sealed class Runtime(List<string> events) : IPoseImportRuntime
    {
        private PoseImportOperation? _current;
        private Action<bool>? _finished;
        private Action<OperationReceipt>? _receipt;
        private readonly List<(int Ticks, Action Action)> _queue = [];
        public readonly List<(PoseImportOperation Operation, bool Expression)> Applied = [];
        public bool IsPending => _current != null;
        public bool IsFrameworkThread => true;
        public bool FreezeOnImport => false;
        public bool IsCurrent(PoseImportOperation operation) => ReferenceEquals(_current, operation);
        public GestureResult Reserve(ActorId actor, string description,
            out PoseImportOperation? operation, Action<bool> onFinished, Action<OperationReceipt> onReceipt)
        {
            var pending = OperationReceipt.Pending(Guid.NewGuid(), OperationEpoch.First,
                SessionGeneration.New(), actor);
            operation = _current = new(pending);
            _finished = onFinished;
            _receipt = onReceipt;
            return GestureResult.Ok() with { OperationReceipt = pending };
        }
        public GestureResult Begin(PoseImportOperation operation, IPreparedPoseImport plan,
            bool expression, bool suppressHistory, string? asset)
        {
            Assert.True(IsCurrent(operation));
            events.Add("apply");
            Applied.Add((operation, expression));
            return GestureResult.Ok();
        }
        public void Finish(bool success) => Terminal(success
            ? OperationReceiptState.Applied : OperationReceiptState.RolledBack);
        private OperationReceipt Terminal(OperationReceiptState state)
        {
            var p = _current!.Pending;
            var terminal = OperationReceipt.Create(p.OperationId, p.OperationEpoch,
                p.SessionGeneration, p.TargetActorId, state, "Test completion");
            _current = null;
            _receipt!(terminal);
            _finished!(state == OperationReceiptState.Applied);
            return terminal;
        }
        public GestureResult CancelActive(string detail) =>
            GestureResult.Fail(detail) with { OperationReceipt = Terminal(OperationReceiptState.Cancelled) };
        public void Schedule(Action action, int ticks) => _queue.Add((ticks, action));
        public void Tick(int ticks)
        {
            var ready = _queue.Where(q => q.Ticks == ticks).ToArray();
            _queue.RemoveAll(q => q.Ticks == ticks);
            foreach (var q in ready) q.Action();
        }
        public void Report(string message, bool error = false) { }
    }
}
