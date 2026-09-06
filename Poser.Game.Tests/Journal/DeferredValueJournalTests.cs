using Poser.Application.Transforms;

namespace Poser.Game.Tests.Journal;

public sealed class DeferredValueJournalTests
{
    [Fact]
    public void Live_updates_append_only_once_when_the_control_commits()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 4;
        ValueWriteResult Write(int next) { value = next; return ValueWriteResult.Ok(); }
        for (int next = 5; next <= 30; next++)
        {
            journal.Adjust("swivel", "Set IK", () => value, Write, next);
            Assert.Equal(next, value);
            Assert.False(history.CanUndo);
        }
        journal.Seal();
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(4, value);
        history.CommitUndo(step);
        Assert.False(history.CanUndo);
        Assert.True(step.Redo());
        Assert.Equal(30, value);
    }

    [Fact]
    public void Returning_to_the_original_value_appends_nothing()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 4;
        ValueWriteResult Write(int next) { value = next; return ValueWriteResult.Ok(); }
        journal.Adjust("swivel", "Set IK", () => value, Write, 30);
        journal.Adjust("swivel", "Set IK", () => value, Write, 4);
        journal.Seal();
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void A_second_drag_has_its_own_before_value_and_refused_updates_do_not_replace_it()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 4;
        ValueWriteResult Write(int next)
        {
            if (next == 99) return new(false, "Rejected");
            value = next;
            return ValueWriteResult.Ok();
        }
        journal.Adjust("swivel", "Set IK", () => value, Write, 30);
        journal.Seal();
        journal.Adjust("swivel", "Set IK", () => value, Write, 40);
        Assert.False(journal.Adjust("swivel", "Set IK", () => value, Write, 99).Success);
        journal.Seal();
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(30, value);
        history.CommitUndo(step);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void Immediate_actions_commit_an_earlier_numeric_edit_before_their_own_entry()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 4;
        ValueWriteResult Write(int next) { value = next; return ValueWriteResult.Ok(); }
        journal.Adjust("swivel", "Set IK", () => value, Write, 30);
        journal.Record("Toggle", false, true, _ => { });
        var toggle = Assert.IsType<JournalStep>(history.PeekUndo());
        history.CommitUndo(toggle);
        Assert.True(Assert.IsType<JournalStep>(history.PeekUndo()).Undo());
        Assert.Equal(4, value);
    }
}
