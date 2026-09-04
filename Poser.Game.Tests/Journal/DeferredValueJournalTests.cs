using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Game.Tests.Journal;

public sealed class DeferredValueJournalTests
{
    [Fact]
    public void Dead_generation_deferred_step_is_no_op_and_never_calls_its_writer()
    {
        var world = new World(); world.Set(1);
        world.Alive = false;
        Assert.True(world.Journal.Undo().Success);
        Assert.Null(world.Pending);
        Assert.Equal(1, world.Value);
        Assert.True(world.History.CanRedo);
        Assert.True(world.Journal.Redo().Success);
        Assert.Null(world.Pending);
        Assert.Equal(0, world.Writes);
    }
    [Fact]
    public void Real_runner_refuses_direct_deferred_dispatch_and_reentrant_commit()
    {
        var history = new TransformHistory();
        using var runner = new TransformGestureService(new SceneSession(new SelectionSession()), new NoRuntime(), history);
        int writes = 0;
        var step = new JournalStep("Colour", () => { writes++; return true; }, () => { writes++; return true; })
        {
            DeferredUndo = (_, _) => { }, DeferredRedo = (_, _) => { },
        };
        history.Append(step);
        Assert.False(runner.Undo().Success);
        history.CommitUndo(step);
        Assert.False(runner.Redo().Success);
        Assert.True(runner.RunDeferredTransition(() =>
            Assert.False(runner.RunDeferredTransition(() => writes++).Success)).Success);
        Assert.Equal(0, writes);
    }

    private sealed class NoRuntime : ITransformRuntimePort
    {
        public TransformPortResult Capture(TransformTargetId target) => throw new InvalidOperationException();
        public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired, bool rawBaseline = false) => throw new InvalidOperationException();
        public TransformPortResult Restore(TransformTargetState state) => throw new InvalidOperationException();
    }
    [Fact]
    public void Delayed_commit_moves_exactly_once_and_duplicate_callbacks_are_ignored()
    {
        var world = new World();
        world.Set(1);
        var step = world.History.PeekUndo();
        Assert.True(world.Journal.Undo().IsPending);
        Assert.Same(step, world.History.PeekUndo());
        var pending = world.Pending!;
        pending.Land(); pending.Land();
        Assert.Equal(0, world.Value);
        Assert.Same(step, world.History.PeekRedo());
        Assert.False(world.Journal.IsRestoring);
        Assert.Equal(1, world.Writes);
        Assert.Empty(world.Notices);
    }

    [Fact]
    public void Inline_completion_moves_once_without_reentering_the_start_guard()
    {
        var world = new World { Immediate = true };
        world.Set(1);
        Assert.True(world.Journal.Undo().Success);
        Assert.Equal(0, world.Value);
        Assert.True(world.Journal.Redo().Success);
        Assert.Equal(1, world.Value);
        Assert.Equal(4, world.Runner.Transitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_gesture_refuses_at_start_or_before_final_mutation(bool afterStart)
    {
        var world = new World();
        world.Set(1);
        if (afterStart) Assert.True(world.Journal.Undo().IsPending);
        world.Runner.Active = true;
        if (afterStart) world.Pending!.Land();
        else Assert.False(world.Journal.Undo().Success);
        Assert.Equal(1, world.Value);
        Assert.True(world.History.CanUndo);
        Assert.False(world.History.CanRedo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Append_or_fold_invalidates_the_pending_ownership_commit(bool fold)
    {
        var world = new World();
        world.Set(1);
        Assert.True(world.Journal.Undo().IsPending);
        if (fold) world.Set(2);
        else world.History.Append(new JournalStep("Other", () => true, () => true));
        world.Pending!.Land();
        Assert.Equal(fold ? 2 : 1, world.Value);
        Assert.Equal(0, world.Writes);
        Assert.False(world.History.CanRedo);
    }

    [Fact]
    public void Clear_then_new_pending_is_not_completed_or_cleared_by_old_callback()
    {
        var world = new World();
        world.Set(1); world.Journal.Undo();
        var old = world.Pending!;
        world.History.Clear();
        world.Set(2); world.Journal.Undo();
        var current = world.Pending!;
        old.Land();
        Assert.True(world.Journal.IsRestoring);
        Assert.Equal(2, world.Value);
        current.Land();
        Assert.False(world.Journal.IsRestoring);
        Assert.Equal(1, world.Value);
    }

    [Fact]
    public void Refusal_and_exception_retain_the_step_for_retry()
    {
        var world = new World();
        world.Set(1);
        var step = world.History.PeekUndo();
        world.Journal.Undo(); world.Pending!.Done(new(false, "foreign hold"));
        Assert.Same(step, world.History.PeekUndo());
        world.Throw = true;
        Assert.False(world.Journal.Undo().Success);
        Assert.Same(step, world.History.PeekUndo());
        world.Throw = false;
        world.Journal.Undo(); world.Pending!.Land();
        Assert.Same(step, world.History.PeekRedo());
    }

    private sealed record PendingWrite(Func<Action, ValueWriteResult> Commit,
        Action<ValueWriteResult> Done, Action Write)
    {
        public void Land() => Done(Commit(Write));
    }

    private sealed class World
    {
        public readonly TransformHistory History = new();
        public readonly Runner Runner = new();
        public readonly List<string> Notices = new();
        public readonly ValueJournal Values;
        public readonly UndoJournal Journal;
        public PendingWrite? Pending;
        public int Value;
        public int Writes;
        public bool Immediate;
        public bool Throw;
        public bool Alive = true;
        public World()
        {
            Values = new(History);
            Journal = new(History, Runner, new Keys(), new Lazy<IPoseSnapshotPort>(() => throw new Exception()),
                _ => true, Notices.Add);
        }
        public void Set(int value) => Values.TrySet("colour", "Colour", () => Value,
            next => { Value = next; return ValueWriteResult.Ok(); }, value,
            alive: () => Alive,
            deferred: (next, commit, done) =>
            {
                if (Throw) throw new InvalidOperationException("request failed");
                Pending = new(commit, done, () => { Value = next; Writes++; });
                if (Immediate) Pending.Land();
            });
    }

    private sealed class Keys : IActorStateKeySource
    {
        public ActorStateKey? Current(Guid lineage) => null;
    }
    private sealed class Runner : IUndoRunner
    {
        public bool Active;
        private bool _busy;
        public int Transitions;
        public GestureResult Undo() => throw new InvalidOperationException("Deferred dispatch bypassed");
        public GestureResult Redo() => throw new InvalidOperationException("Deferred dispatch bypassed");
        public GestureResult RunDeferredTransition(Action action)
        {
            if (Active || _busy) return GestureResult.Fail("busy");
            _busy = true;
            try { Transitions++; action(); return GestureResult.Ok(); }
            catch (Exception ex) { return GestureResult.Fail(ex.Message); }
            finally { _busy = false; }
        }
    }
}
