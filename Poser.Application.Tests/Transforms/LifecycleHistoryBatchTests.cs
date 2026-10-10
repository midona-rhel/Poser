using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class LifecycleHistoryBatchTests
{
    [Fact]
    public void Mixed_removals_publish_once_and_replay_reverse_then_forward()
    {
        var history = new TransformHistory();
        var calls = new List<string>();
        var published = new List<HistoryEntry>();
        history.Appended += published.Add;
        history.RecordLifecycleBatch("Remove selection", () =>
        {
            history.Append(new SceneLifecyclePatch("Remove prop", () => Add("undo prop"), () => Add("redo prop")));
            history.Append(new JournalStep("Release actor", () => Add("undo actor"), () => Add("redo actor")));
        });
        bool Add(string value) { calls.Add(value); return true; }

        Assert.Equal("Remove selection", Assert.Single(published).Description);
        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.True(entry.Undo());
        history.CommitUndo(entry);
        Assert.False(history.CanUndo);
        Assert.Equal(new[] { "undo actor", "undo prop" }, calls);
        Assert.True(entry.Redo());
        history.CommitRedo(entry);
        Assert.Equal(new[] { "undo actor", "undo prop", "redo prop", "redo actor" }, calls);
    }

    [Fact]
    public void Retry_does_not_repeat_siblings_that_already_landed()
    {
        var history = new TransformHistory();
        int earlier = 0, later = 0;
        bool available = false;
        history.RecordLifecycleBatch("Remove selection", () =>
        {
            history.Append(new SceneLifecyclePatch("Earlier", () => { earlier++; return available; }, () => true)
            { FailureDetail = () => "Waiting for earlier entity" });
            history.Append(new JournalStep("Later", () => { later++; return true; }, () => true));
        });
        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.False(entry.Undo());
        Assert.Equal("Waiting for earlier entity", entry.FailureDetail!());
        Assert.Equal(RefusalAction.Keep, RefusalPolicy.Decide(entry));
        available = true;
        Assert.True(entry.Undo());
        Assert.Equal(2, earlier);
        Assert.Equal(1, later);
        Assert.True(entry.Redo());
        Assert.True(entry.Undo());
        Assert.Equal(2, later);
    }

    [Fact]
    public void Same_refusal_policy_drives_undo_journal_and_batch()
    {
        static JournalStep Refusing(RefusalAction action) =>
            new(action.ToString(), () => false, () => true)
                { OnRefusal = () => action, FailureDetail = () => $"{action} refused" };
        Assert.Equal(RefusalAction.DropOnRepeat, RefusalPolicy.Decide(new JournalStep("Plain", () => false, () => true)));
        Assert.Equal(RefusalAction.Keep, RefusalPolicy.Decide(new SceneLifecyclePatch("Lifecycle", () => false, () => true)));

        foreach (var (action, expectedRefusals) in new[]
            { (RefusalAction.Keep, 3), (RefusalAction.DropOnRepeat, 2), (RefusalAction.DropNow, 1) })
        {
            // Undo journal: count refusals before the entry leaves history.
            var history = new TransformHistory();
            var notices = new List<string>();
            var journal = new UndoJournal(history, new Runner(history), _ => true, notices.Add);
            history.Append(Refusing(action));
            int journalRefusals = 0;
            while (history.CanUndo && journalRefusals < 3)
            {
                Assert.False(journal.Undo().Success);
                journalRefusals++;
            }

            // Batch: the same step as the only child of a lifecycle batch.
            var batchHistory = new TransformHistory();
            batchHistory.RecordLifecycleBatch("Batch", () => batchHistory.Append(Refusing(action)));
            var batch = Assert.IsType<SceneLifecyclePatch>(batchHistory.PeekUndo());
            int batchRefusals = 0;
            while (batchRefusals < 3)
            {
                Assert.False(batch.Undo());
                batchRefusals++;
                Assert.Equal($"{action} refused", batch.FailureDetail!());
                if (RefusalPolicy.Decide(batch) == RefusalAction.DropNow) break;
            }

            Assert.Equal(expectedRefusals, journalRefusals);
            Assert.Equal(expectedRefusals, batchRefusals);
        }
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
            var entry = (InverseEntry)(before ? history.PeekUndo(entity) : history.PeekRedo(entity))!;
            if (!(before ? entry.Undo() : entry.Redo())) return GestureResult.Fail("Refused");
            if (before) history.CommitUndo(entry, entity); else history.CommitRedo(entry, entity);
            return GestureResult.Ok();
        }
    }

    [Fact]
    public void Command_exception_preserves_already_recorded_removals_and_ends_capture()
    {
        var history = new TransformHistory();
        int restored = 0;
        Assert.Throws<InvalidOperationException>(() => history.RecordLifecycleBatch("Remove selection", () =>
        {
            history.Append(new SceneLifecyclePatch("Removed", () => { restored++; return true; }, () => true));
            throw new InvalidOperationException("Next removal failed");
        }));
        var batch = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.True(batch.Undo());
        Assert.Equal(1, restored);
        var later = new JournalStep("Unrelated later edit", () => true, () => true);
        history.Append(later);
        Assert.Same(later, history.PeekUndo());
    }
}
