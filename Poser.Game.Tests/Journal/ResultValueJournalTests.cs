using Poser.Application.Transforms;
using Poser.Domain.Transforms;

namespace Poser.Game.Tests.Journal;

public sealed class ResultValueJournalTests
{
    private sealed class Target
    {
        public int Value;
        public bool Reject;
        public bool Alive = true;
        public int Writes;
        public ValueWriteResult Write(int value)
        {
            Writes++;
            if (Reject) return new(false, "Foreign appearance hold");
            Value = value;
            return ValueWriteResult.Ok();
        }
    }

    private static ValueWriteResult Set(ValueJournal journal, Target target, int value)
        => journal.TrySet(target, "Colour", () => target.Value, target.Write, value, () => target.Alive);

    [Fact]
    public void Initial_failure_does_not_append_and_keeps_detail()
    {
        var history = new TransformHistory();
        var target = new Target { Reject = true };
        var result = Set(new ValueJournal(history), target, 8);
        Assert.False(result.Success);
        Assert.Equal("Foreign appearance hold", result.Detail);
        Assert.False(history.CanUndo);
        Assert.Equal(0, target.Value);
    }

    [Fact]
    public void Failed_fold_keeps_last_success_and_successful_fold_keeps_original_before()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();
        int folds = 0;
        journal.Folded += (_, _) => folds++;
        Assert.True(Set(journal, target, 7).Success);
        Assert.True(Set(journal, target, 8).Success);
        target.Reject = true;
        Assert.False(Set(journal, target, 9).Success);
        Assert.Equal(1, folds);
        target.Reject = false;
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(0, target.Value);
        history.CommitUndo(step);
        Assert.False(history.CanUndo);
        Assert.True(step.Redo());
        Assert.Equal(8, target.Value);
    }

    [Fact]
    public void Repeated_failed_inverse_never_drops_or_advances_and_can_retry()
    {
        var history = new TransformHistory();
        var target = new Target();
        Set(new ValueJournal(history), target, 8);
        var undo = new UndoJournal(history, new Runner(history), null!, null!, _ => true, _ => { });
        target.Reject = true;
        for (int i = 0; i < 3; i++)
        {
            var refused = undo.Undo();
            Assert.False(refused.Success);
            Assert.Equal("Foreign appearance hold", refused.Detail);
            Assert.True(history.CanUndo);
            Assert.False(history.CanRedo);
        }
        target.Reject = false;
        Assert.True(undo.Undo().Success);
        target.Reject = true;
        for (int i = 0; i < 3; i++) Assert.False(undo.Redo().Success);
        Assert.True(history.CanRedo);
        Assert.False(history.CanUndo);
        target.Reject = false;
        Assert.True(undo.Redo().Success);
        Assert.Equal(8, target.Value);
    }

    [Fact]
    public void Suspension_writes_without_history_and_dead_generation_inverse_is_noop()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();
        using (journal.Suspend()) Assert.True(Set(journal, target, 7).Success);
        Assert.False(history.CanUndo);
        Assert.True(Set(journal, target, 8).Success);
        int writes = target.Writes;
        target.Alive = false;
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.True(step.Redo());
        Assert.Equal(writes, target.Writes);
        Assert.Equal(8, target.Value);
    }

    [Fact]
    public void Sealed_palette_picks_are_distinct_steps()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();
        Set(journal, target, 7);
        journal.Seal();
        Set(journal, target, 8);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        history.CommitUndo(step);
        Assert.Equal(7, target.Value);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void Recorded_transaction_refuses_inverse_and_failed_new_edit_keeps_redo()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target { Value = 8 };
        journal.RecordResult("Body", 0, 8, target.Write, () => target.Alive);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        target.Reject = true;
        Assert.False(step.Undo());
        Assert.Equal("Foreign appearance hold", step.FailureDetail?.Invoke());
        target.Reject = false;
        Assert.True(step.Undo());
        history.CommitUndo(step);
        target.Reject = true;
        Assert.False(Set(journal, target, 9).Success);
        Assert.Same(step, history.PeekRedo());
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Thrown_write_and_suspended_refusal_are_failures_without_steps()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var thrown = journal.TrySet("key", "Colour", () => 0,
            _ => throw new InvalidOperationException("read/write boundary failed"), 1);
        Assert.False(thrown.Success);
        Assert.Equal("read/write boundary failed", thrown.Detail);
        using (journal.Suspend())
            Assert.False(Set(journal, new Target { Reject = true }, 8).Success);
        Assert.False(history.CanUndo);
    }

    private sealed class Runner(TransformHistory history) : IUndoRunner
    {
        public GestureResult Undo() => Run(true);
        public GestureResult Redo() => Run(false);
        private GestureResult Run(bool before)
        {
            var step = (JournalStep)(before ? history.PeekUndo()! : history.PeekRedo()!);
            if (!(before ? step.Undo() : step.Redo()))
                return GestureResult.Fail(step.FailureDetail?.Invoke() ?? "failed");
            if (before) history.CommitUndo(step); else history.CommitRedo(step);
            return GestureResult.Ok();
        }
    }
}
