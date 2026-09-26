using Poser.Application.Transforms;
using Poser.Domain.Transforms;

namespace Poser.Game.Tests.Journal;

public sealed class UndoJournalTests
{
    [Fact]
    public void Ordinary_edits_use_their_recorded_inverse()
    {
        var world = new World();
        int value = 2;
        var step = new JournalStep("Value", () => { value = 1; return true; }, () => { value = 2; return true; });
        world.History.Append(step);
        Assert.True(world.Journal.Undo().Success);
        Assert.Equal(1, value);
        Assert.True(world.Journal.Redo().Success);
        Assert.Equal(2, value);
        Assert.Empty(world.Notices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redo_refuses_when_the_required_file_is_gone(bool deferred)
    {
        var world = new World(assetExists: false);
        bool ran = false;
        var step = new JournalStep("Import", () => true, () => { ran = true; return true; })
        {
            RequiredAsset = "gone.pose",
            CompleteReplay = deferred ? (_, _, _, done) => done(GestureResult.Ok()) : null,
        };
        world.History.Append(step);
        world.History.CommitUndo(step);
        Assert.False(world.Journal.Redo().Success);
        Assert.False(ran);
        Assert.Same(step, world.History.PeekRedo());
        Assert.Equal([UndoJournal.AssetGone], world.Notices);
    }

    [Fact]
    public void Nonretryable_lifecycle_refusal_is_reported_and_dropped_so_older_history_runs()
    {
        var world = new World();
        var earlier = new JournalStep("Earlier edit", () => true, () => true);
        var refused = new SceneLifecyclePatch("Release world object", () => false, () => true)
        {
            FailureDetail = () => "The borrowed object is no longer available.",
            DropOnFailure = () => true,
        };
        world.History.Append(earlier);
        world.History.Append(refused);
        Assert.False(world.Journal.Undo().Success);
        Assert.Same(earlier, world.History.PeekUndo());
        Assert.Equal("The borrowed object is no longer available.", Assert.Single(world.Notices));
        Assert.True(world.Journal.Undo().Success);
        Assert.False(world.History.CanUndo);
    }

    [Fact]
    public void Explicit_restoration_waits_for_completion_in_both_directions()
    {
        var world = new World();
        int inverse = 0, redo = 0;
        Action<GestureResult>? complete = null;
        var step = new JournalStep("Reset all", () => { inverse++; return true; }, () => { redo++; return true; })
        {
            CompleteReplay = (_, _, _, done) => complete = done,
        };
        world.History.Append(step);
        Assert.True(world.Journal.Undo().Success);
        Assert.Equal(1, inverse);
        Assert.Same(step, world.History.PeekUndo());
        Assert.False(world.Journal.CanUndo);
        Assert.False(world.Journal.Redo().Success);
        complete!(GestureResult.Ok());
        Assert.Same(step, world.History.PeekRedo());
        Assert.False(world.Journal.IsRestoring);
        Assert.True(world.Journal.Redo().Success);
        Assert.Equal(1, redo);
        Assert.Same(step, world.History.PeekRedo());
        complete!(GestureResult.Ok());
        Assert.Same(step, world.History.PeekUndo());
    }

    [Fact]
    public void Failed_restoration_does_not_advance_history_and_can_be_retried()
    {
        var world = new World();
        Action<GestureResult>? complete = null;
        var step = new JournalStep("Reset all", () => true, () => true)
        {
            CompleteReplay = (_, _, _, done) => complete = done,
            RetainOnFailure = true,
        };
        world.History.Append(step);
        Assert.True(world.Journal.Undo().Success);
        complete!(GestureResult.Fail("The actor is redrawing."));
        Assert.Same(step, world.History.PeekUndo());
        Assert.False(world.Journal.IsRestoring);
        Assert.Equal(["The actor is redrawing."], world.Notices);
        Assert.True(world.Journal.Undo().Success);
        complete!(GestureResult.Ok());
        Assert.Same(step, world.History.PeekRedo());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void History_changes_invalidate_waiting_restore_without_advancing_new_entry(bool clear)
    {
        var world = new World();
        Action<GestureResult>? complete = null;
        Func<bool>? current = null;
        CancellationToken token = default;
        var step = new JournalStep("Redraw", () => true, () => true)
        {
            CompleteReplay = (_, valid, cancellation, done) => { current = valid; token = cancellation; complete = done; },
        };
        world.History.Append(step);
        Assert.True(world.Journal.Undo().Success);
        if (clear) world.History.Clear();
        var later = new JournalStep("Later", () => true, () => true);
        world.History.Append(later);
        Assert.False(current!());
        Assert.Equal(clear, token.IsCancellationRequested);
        complete!(GestureResult.Fail("No longer current."));
        Assert.Same(later, world.History.PeekUndo());
        Assert.False(world.History.CanRedo);
        Assert.False(world.Journal.IsRestoring);
    }

    private sealed class World
    {
        public TransformHistory History { get; } = new();
        public List<string> Notices { get; } = new();
        public UndoJournal Journal { get; }
        public World(bool assetExists = true) =>
            Journal = new(History, new Runner(History), _ => assetExists, Notices.Add);
    }

    private sealed class Runner(TransformHistory history) : IUndoRunner
    {
        public GestureResult Undo() => Apply(true);
        public GestureResult Redo() => Apply(false);
        public GestureResult Replay(JournalStep step, bool before) =>
            (before ? step.Undo() : step.Redo()) ? GestureResult.Ok() : GestureResult.Fail("Refused");
        private GestureResult Apply(bool before)
        {
            var entry = before ? history.PeekUndo() : history.PeekRedo();
            bool success = entry switch
            {
                JournalStep step => before ? step.Undo() : step.Redo(),
                SceneLifecyclePatch step => before ? step.Undo() : step.Redo(),
                _ => false,
            };
            if (!success) return GestureResult.Fail("Refused");
            if (before) history.CommitUndo(entry!); else history.CommitRedo(entry!);
            return GestureResult.Ok();
        }
    }
}
