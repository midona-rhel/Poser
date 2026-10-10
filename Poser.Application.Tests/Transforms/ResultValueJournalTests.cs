using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class ResultValueJournalTests
{
    private sealed class Target
    {
        public int Value;
        public bool Reject;
        public bool Permanent;
        public bool Alive = true;
        public int Writes;
        public Outcome Write(int value)
        {
            Writes++;
            if (Reject && Permanent) return new(false, "The game did not take the model.");
            if (Reject) return Outcome.Busy("Foreign appearance hold");
            Value = value;
            return Outcome.Ok();
        }
    }

    private static Outcome Set(ValueJournal journal, Target target, int value)
        => journal.Set(target, "Colour", () => target.Value, target.Write, value, () => target.Alive);

    [Fact]
    public void Failed_live_write_keeps_last_success_and_original_before_until_commit()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();
        journal.BeginEdit("colour");
        Assert.True(Set(journal, target, 7).Success);
        Assert.True(Set(journal, target, 8).Success);
        target.Reject = true;
        Assert.False(Set(journal, target, 9).Success);
        journal.EndEdit();
        Assert.False(history.CanUndo);
        journal.Seal();
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
    public void Repeated_transient_refusal_never_drops_or_advances_and_can_retry()
    {
        var history = new TransformHistory();
        var target = new Target();
        Set(new ValueJournal(history), target, 8);
        var undo = new UndoJournal(history, new Runner(history), _ => true, _ => { });
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
    public void Permanent_refusal_is_reported_then_dropped_on_repeat_so_earlier_undo_proceeds()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var earlier = new Target();
        var target = new Target();
        Set(journal, earlier, 5);
        Set(journal, target, 8);
        var notices = new List<string>();
        var undo = new UndoJournal(history, new Runner(history), _ => true, notices.Add);
        target.Reject = target.Permanent = true;
        var refused = undo.Undo();
        Assert.False(refused.Success);
        Assert.Equal("The game did not take the model.", refused.Detail);
        Assert.Empty(notices);
        Assert.False(undo.Undo().Success);
        Assert.Single(notices);
        Assert.True(undo.Undo().Success);
        Assert.Equal(0, earlier.Value);
        Assert.Equal(8, target.Value);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Refused_write_leaves_history_and_redo_unchanged()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();
        Set(journal, target, 7);
        Set(journal, target, 8);
        var undone = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(undone.Undo());
        history.CommitUndo(undone);
        var top = history.PeekUndo();
        target.Reject = true;
        var refused = Set(journal, target, 9);
        Assert.False(refused.Success);
        Assert.Equal("Foreign appearance hold", refused.Detail);
        Assert.Same(top, history.PeekUndo());
        Assert.Same(undone, history.PeekRedo());
        Assert.Equal(7, target.Value);
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
