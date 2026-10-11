using Poser.Application.Transforms;
using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Transforms;

public sealed class UndoJournalTests
{
    [Fact]
    public void Selected_history_never_removes_its_live_target_but_global_lifecycle_roundtrips()
    {
        var world = new World();
        var light = SelectionId.ForLight(LightId.New());
        bool alive = true;
        int intensity = 2;
        var creation = new SceneLifecyclePatch("Add light",
            () => { alive = false; return true; },
            () => { alive = true; return true; })
            { AffectedEntities = new[] { light } };
        world.History.Append(creation);
        var edit = new JournalStep("Intensity",
            () => { intensity = 1; return true; },
            () => { intensity = 2; return true; })
            { AffectedEntities = new[] { light } };
        world.History.Append(edit);
        Assert.True(world.Journal.Undo(light).Success);
        Assert.Equal(1, intensity);
        Assert.False(world.Journal.Undo(light).Success);
        Assert.True(alive);
        Assert.Same(creation, world.History.PeekUndo());
        Assert.Same(edit, world.History.PeekRedo(light));
        Assert.True(world.Journal.Redo(light).Success);
        Assert.Equal(2, intensity);

        Assert.True(world.Journal.Undo(light).Success);
        Assert.True(world.Journal.Undo().Success);
        Assert.False(alive);
        Assert.False(world.Journal.Redo(light).Success); // cannot bypass creation
        Assert.False(alive);
        Assert.True(world.Journal.Redo().Success);
        Assert.True(alive);
        Assert.True(world.Journal.Redo(light).Success);
        Assert.Equal(2, intensity);
    }

    [Fact]
    public void Redo_refuses_when_the_required_file_is_gone()
    {
        var world = new World(assetExists: false);
        bool ran = false;
        var step = new JournalStep("Import", () => true, () => { ran = true; return true; })
        {
            RequiredAsset = "gone.pose",
            CompleteReplay = (_, _, _, done) => done(GestureResult.Ok()),
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
            OnRefusal = () => RefusalAction.DropNow,
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

        // A history change invalidates the waiting restore; it never advances the new entry.
        Func<bool>? current = null;
        CancellationToken token = default;
        var waiting = new JournalStep("Redraw", () => true, () => true)
        {
            CompleteReplay = (_, valid, cancellation, done) => { current = valid; token = cancellation; complete = done; },
        };
        world.History.Append(waiting);
        Assert.True(world.Journal.Undo().Success);
        var later = new JournalStep("Later", () => true, () => true);
        world.History.Append(later);
        Assert.False(current!());
        Assert.True(token.IsCancellationRequested);
        complete!(GestureResult.Fail("No longer current."));
        Assert.Same(later, world.History.PeekUndo());
        Assert.False(world.History.CanRedo);
        Assert.False(world.Journal.IsRestoring);
    }

    [Fact]
    public void Failed_restoration_does_not_advance_history_and_can_be_retried()
    {
        var world = new World();
        Action<GestureResult>? complete = null;
        var step = new JournalStep("Reset all", () => true, () => true)
        {
            CompleteReplay = (_, _, _, done) => complete = done,
            OnRefusal = () => RefusalAction.Keep,
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

    private sealed class World
    {
        public EditHistory History { get; } = new();
        public Fixtures.NoticeLog Notices { get; } = new();
        public UndoJournal Journal { get; }
        public World(bool assetExists = true) =>
            Journal = new(History, new Runner(History), _ => assetExists, Notices);
    }

    private sealed class Runner(EditHistory history) : IUndoRunner
    {
        public GestureResult? RecoverPending() => null;
        public GestureResult Replay(JournalStep step, bool before, SelectionId? entity) =>
            (before ? step.Undo() : step.Redo()) ? GestureResult.Ok() : GestureResult.Fail("Refused");
        public GestureResult Run(HistoryEntry entry, bool undo, SelectionId? entity)
        {
            bool success = entry switch
            {
                JournalStep step => undo ? step.Undo() : step.Redo(),
                SceneLifecyclePatch step => undo ? step.Undo() : step.Redo(),
                _ => false,
            };
            if (!success) return GestureResult.Fail("Refused");
            if (undo) history.CommitUndo(entry, entity); else history.CommitRedo(entry, entity);
            return GestureResult.Ok();
        }
    }
}
