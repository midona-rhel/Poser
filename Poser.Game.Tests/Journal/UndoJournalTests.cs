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
    }

    private sealed class FakeKeys(ActorStateKey? current) : IActorStateKeySource
    {
        public ActorStateKey? Current(Guid lineage) => current;
    }

    private sealed class FakeSnapshots : IPoseSnapshotPort
    {
        public List<string> Restored { get; } = new();

        public ActorSnapshot? Capture(Guid lineage) => Snapshot("captured");

        public bool Restore(ActorSnapshot snapshot, Action<bool> finished)
        {
            Restored.Add((string)snapshot.Pose);
            finished(true);
            return true;
        }
    }
}
