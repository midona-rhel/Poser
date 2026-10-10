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
        Assert.True(token.IsCancellationRequested);
        complete!(GestureResult.Fail("No longer current."));
        Assert.Same(later, world.History.PeekUndo());
        Assert.False(world.History.CanRedo);
        Assert.False(world.Journal.IsRestoring);
    }

    [Fact]
    public void Scoped_deferred_restore_preserves_unrelated_newer_entry_until_completion()
    {
        var world = new World();
        var actor = SelectionId.ForActor(ActorId.New());
        Action<GestureResult>? complete = null;
        var step = new JournalStep("Actor reset", () => true, () => true)
        {
            AffectedEntities = new[] { actor },
            CompleteReplay = (_, _, _, done) => complete = done,
        };
        var light = new JournalStep("Light", () => true, () => true)
            { AffectedEntities = new[] { SelectionId.ForLight(LightId.New()) } };
        world.History.Append(step); world.History.Append(light);
        Assert.True(world.Journal.Undo(actor).Success);
        Assert.True(world.Journal.IsRestoring);
        Assert.Same(light, world.History.PeekUndo());
        complete!(GestureResult.Ok());
        Assert.Same(light, world.History.PeekUndo());
        Assert.Same(step, world.History.PeekRedo(actor));
        Assert.True(world.Journal.Redo(actor).Success);
        complete!(GestureResult.Ok());
        Assert.Same(step, world.History.PeekUndo());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void New_disjoint_edit_invalidates_pending_scoped_replay(bool undo)
    {
        var world = new World();
        var actor = SelectionId.ForActor(ActorId.New());
        var light = SelectionId.ForLight(LightId.New());
        Func<bool>? current = null;
        Action<GestureResult>? complete = null;
        CancellationToken token = default;
        var step = new JournalStep("Actor reset", () => true, () => true)
        {
            AffectedEntities = new[] { actor },
            CompleteReplay = (_, valid, cancellation, done) =>
                { current = valid; token = cancellation; complete = done; },
        };
        world.History.Append(step);
        if (!undo) world.History.CommitUndo(step);
        Assert.True((undo ? world.Journal.Undo(actor) : world.Journal.Redo(actor)).Success);
        Assert.True(current!());
        var later = new JournalStep("Light edit", () => true, () => true)
            { AffectedEntities = new[] { light } };
        world.History.Append(later);
        Assert.True(token.IsCancellationRequested);
        Assert.False(current());
        if (undo) Assert.Same(step, world.History.PeekUndo(actor));
        complete!(GestureResult.Ok()); // even a late success cannot commit stale history
        Assert.False(world.Journal.IsRestoring);
        Assert.Same(later, world.History.PeekUndo());
        Assert.False(world.History.CanRedo);
        Assert.Equal(UndoJournal.Dropped, Assert.Single(world.Notices));
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
        public GestureResult Undo(SelectionId entity) => Apply(true, entity);
        public GestureResult Redo(SelectionId entity) => Apply(false, entity);
        public GestureResult Replay(JournalStep step, bool before, SelectionId entity) => Replay(step, before);
        public GestureResult Replay(JournalStep step, bool before) =>
            (before ? step.Undo() : step.Redo()) ? GestureResult.Ok() : GestureResult.Fail("Refused");
        private GestureResult Apply(bool before, SelectionId? entity = null)
        {
            var entry = before ? history.PeekUndo(entity) : history.PeekRedo(entity);
            bool success = entry switch
            {
                JournalStep step => before ? step.Undo() : step.Redo(),
                SceneLifecyclePatch step => before ? step.Undo() : step.Redo(),
                _ => false,
            };
            if (!success) return GestureResult.Fail("Refused");
            if (before) history.CommitUndo(entry!, entity); else history.CommitRedo(entry!, entity);
            return GestureResult.Ok();
        }
    }
}
