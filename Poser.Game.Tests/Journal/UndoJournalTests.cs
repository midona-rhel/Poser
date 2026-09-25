using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Game.Tests.Journal;

public sealed class UndoJournalTests
{
    private static readonly Guid Lineage = Guid.NewGuid();

    private static ActorStateKey Key(uint generation) =>
        new(Lineage, new ActorId(Lineage, generation), Array.Empty<SkeletonId>(), "a", 0);

    private static ActorSnapshot Snapshot(string tag) =>
        new(Lineage, tag, Array.Empty<IkChainSnapshot>());

    [Fact]
    public void Undo_restores_the_snapshot_when_the_actor_key_moved()
    {
        var world = new World(current: Key(2));
        var step = new JournalStep("Move", () => true, () => true)
        {
            Context = new StepContext([Key(1)], [Snapshot("before")], [Snapshot("after")]),
        };
        world.History.Append(step);

        var result = world.Journal.Undo();

        Assert.True(result.Success);
        Assert.Equal(["before"], world.Snapshots.Restored);
        Assert.Equal(0, world.Runner.Undos);
        Assert.True(world.History.CanRedo);
        Assert.False(world.History.CanUndo);
        Assert.Equal([UndoJournal.RestoredFromSnapshot], world.Notices);
    }

    [Fact]
    public void Undo_runs_the_step_when_the_keys_match()
    {
        var world = new World(current: Key(1));
        world.History.Append(new JournalStep("Move", () => true, () => true)
        {
            Context = new StepContext([Key(1)], [Snapshot("before")], [Snapshot("after")]),
        });

        var result = world.Journal.Undo();

        Assert.True(result.Success);
        Assert.Equal(1, world.Runner.Undos);
        Assert.Empty(world.Snapshots.Restored);
        Assert.Empty(world.Notices);
    }

    [Fact]
    public void Redo_refuses_when_the_file_is_gone()
    {
        var world = new World(current: Key(1), assetExists: false);
        var step = new JournalStep("Import pose", () => true, () => true)
        {
            Context = new StepContext([Key(1)], [Snapshot("before")], [Snapshot("after")], "gone.pose"),
        };
        world.History.Append(step);
        world.History.CommitUndo(step);

        var result = world.Journal.Redo();

        Assert.False(result.Success);
        Assert.True(world.History.CanRedo);
        Assert.Equal(0, world.Runner.Redos);
        Assert.Equal([UndoJournal.AssetGone], world.Notices);
    }

    [Fact]
    public void Reconcile_keeps_a_stale_patch_that_carries_a_snapshot_while_the_actor_lineage_lives()
    {
        var history = new TransformHistory();
        var target = TransformTargetId.ForActor(new ActorId(Lineage, 1));
        var state = new TransformTargetState(target, PoseTransform.Identity, new BonePose(), false);
        history.Append(new TransformPatch("Move", [state], [state])
        {
            Context = new StepContext([Key(1)], [Snapshot("before")], [Snapshot("after")]),
        });
        history.Append(new TransformPatch("Bare move", [state], [state]));

        history.Reconcile(_ => false, _ => true);
        Assert.Equal("Move", history.UndoDescription);

        history.Reconcile(_ => false, _ => false);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void The_default_depth_is_five_hundred()
    {
        Assert.Equal(500, TransformHistory.DefaultCapacity);
        Assert.Equal(500, new Poser.Config.PoserConfiguration().UndoDepth);
    }

    [Fact]
    public void Nonretryable_lifecycle_refusal_is_reported_and_dropped_so_older_history_runs()
    {
        var history = new TransformHistory();
        var earlier = new JournalStep("Earlier edit", () => true, () => true);
        var refused = new SceneLifecyclePatch("Remove world object", () => false, () => true)
        {
            FailureDetail = () => "Cannot restore borrowed BG/VFX without a native allocation lease.",
            DropOnFailure = () => true,
        };
        history.Append(earlier);
        history.Append(refused);
        var notices = new List<string>();
        var journal = new UndoJournal(
            history,
            new LifecycleRefusalRunner(history),
            new FakeKeys(Key(1)),
            new Lazy<IPoseSnapshotPort>(() => new FakeSnapshots()),
            _ => true,
            notices.Add);

        Assert.False(journal.Undo().Success);
        Assert.Same(earlier, history.PeekUndo());
        Assert.Equal("Cannot restore borrowed BG/VFX without a native allocation lease.",
            Assert.Single(notices));
        Assert.True(journal.Undo().Success);
        Assert.False(history.CanUndo);
    }

    private sealed class World
    {
        public TransformHistory History { get; } = new();
        public FakeRunner Runner { get; } = new();
        public FakeSnapshots Snapshots { get; } = new();
        public List<string> Notices { get; } = new();
        public UndoJournal Journal { get; }

        public World(ActorStateKey? current, bool assetExists = true)
        {
            Journal = new UndoJournal(
                History,
                Runner,
                new FakeKeys(current),
                new Lazy<IPoseSnapshotPort>(() => Snapshots),
                _ => assetExists,
                Notices.Add)
            {
                // The keys are disconnected by default; these tests are the
                // record of what they do when they are on.
                StateKeys = true,
            };
        }
    }

    private sealed class FakeRunner : IUndoRunner
    {
        public int Undos;
        public int Redos;
        public GestureResult Undo() { Undos++; return GestureResult.Ok(); }
        public GestureResult Redo() { Redos++; return GestureResult.Ok(); }
        public GestureResult Replay(JournalStep step, bool before) =>
            (before ? step.Undo() : step.Redo()) ? GestureResult.Ok() : GestureResult.Fail("Refused");
    }

    private sealed class LifecycleRefusalRunner(TransformHistory history) : IUndoRunner
    {
        private bool _refused;

        public GestureResult Undo()
        {
            if (!_refused)
            {
                _refused = true;
                return GestureResult.Fail("The lifecycle action was refused.");
            }
            var entry = history.PeekUndo()!;
            history.CommitUndo(entry);
            return GestureResult.Ok();
        }

        public GestureResult Redo() => GestureResult.Fail("No redo expected.");
    }

    private sealed class FakeKeys(ActorStateKey? current) : IActorStateKeySource
    {
        public ActorStateKey? Current(Guid lineage) => current;
    }

    private sealed class FakeSnapshots : IPoseSnapshotPort
    {
        public List<string> Restored { get; } = new();
        public bool Deferred;
        public Action<bool>? Complete;
        public Func<bool>? StillCurrent;

        public ActorSnapshot? Capture(Guid lineage) => Snapshot("captured");

        public bool Restore(ActorSnapshot snapshot, Action<bool> finished)
        {
            Restored.Add((string)snapshot.Pose);
            if (Deferred) Complete = finished;
            else finished(true);
            return true;
        }

        public bool Restore(ActorSnapshot snapshot, Func<bool> stillCurrent, Action<bool> finished)
        {
            StillCurrent = stillCurrent;
            return Restore(snapshot, finished);
        }
    }

    [Fact]
    public void Disruptive_undo_runs_inverse_then_waits_for_pose_completion_and_redo_does_the_same()
    {
        var world = new World(Key(2));
        world.Snapshots.Deferred = true;
        int inverse = 0, verb = 0;
        var step = new JournalStep("Set model", () => { inverse++; return true; }, () => { verb++; return true; })
        {
            RestoreSnapshotsAfterReplay = true,
            Context = new([Key(1)], [Snapshot("before")], [Snapshot("after")]),
        };
        world.History.Append(step);

        Assert.True(world.Journal.Undo().Success);
        Assert.Equal(1, inverse); // A moved skeleton key must not skip the model inverse.
        Assert.Same(step, world.History.PeekUndo());
        Assert.False(world.Journal.CanUndo);
        Assert.False(world.Journal.Redo().Success);
        world.Snapshots.Complete!(true);
        Assert.Same(step, world.History.PeekRedo());
        Assert.False(world.Journal.IsRestoring);

        Assert.True(world.Journal.Redo().Success);
        Assert.Equal(1, verb);
        Assert.Same(step, world.History.PeekRedo());
        world.Snapshots.Complete!(true);
        Assert.Same(step, world.History.PeekUndo());
        Assert.Equal(["before", "after"], world.Snapshots.Restored);
        Assert.Empty(world.Notices);
    }

    [Fact]
    public void Failed_import_does_not_advance_history_and_can_be_retried()
    {
        var world = new World(Key(1));
        world.Snapshots.Deferred = true;
        var step = new JournalStep("Reset all", () => true, () => true)
        {
            RestoreSnapshotsAfterReplay = true,
            Context = new([], [Snapshot("before")], []),
        };
        world.History.Append(step);
        Assert.True(world.Journal.Undo().Success);
        world.Snapshots.Complete!(false);
        Assert.Same(step, world.History.PeekUndo());
        Assert.False(world.Journal.IsRestoring);
        Assert.Equal([UndoJournal.RestoreFailed], world.Notices);
        Assert.True(world.Journal.Undo().Success);
        world.Snapshots.Complete!(true);
        Assert.Same(step, world.History.PeekRedo());
        Assert.True(world.Journal.Redo().Success); // Reset's redo has no pose to import.
        Assert.Same(step, world.History.PeekUndo());
        Assert.False(world.Journal.IsRestoring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void History_changes_cancel_waiting_native_restore_without_advancing_new_entry(bool clear)
    {
        var world = new World(Key(1));
        world.Snapshots.Deferred = true;
        var step = new JournalStep("Redraw", () => true, () => true)
        {
            RestoreSnapshotsAfterReplay = true,
            Context = new([], [Snapshot("before")], []),
        };
        world.History.Append(step);
        Assert.True(world.Journal.Undo().Success);
        if (clear) world.History.Clear();
        var later = new JournalStep("Later", () => true, () => true);
        world.History.Append(later);
        Assert.False(world.Snapshots.StillCurrent!());
        world.Snapshots.Complete!(false);
        Assert.Same(later, world.History.PeekUndo());
        Assert.False(world.History.CanRedo);
        Assert.False(world.Journal.IsRestoring);
    }
}
