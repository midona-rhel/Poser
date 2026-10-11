using Poser.Application.Transforms;
using Poser.Domain;

namespace Poser.Application.Tests.Transforms;

public sealed class DeferredValueJournalTests
{
    [Fact]
    public void Immediate_actions_commit_an_earlier_numeric_edit_before_their_own_entry()
    {
        var history = new EditHistory();
        var journal = new ValueJournal(history);
        int value = 4;
        Outcome Write(int next) { value = next; return Outcome.Ok(); }
        journal.Adjust("swivel", "Set IK", () => value, Write, 30);
        journal.Record("toggle", "Toggle", false, true, _ => Outcome.Ok());
        var toggle = Assert.IsType<JournalStep>(history.PeekUndo());
        history.CommitUndo(toggle);
        Assert.True(Assert.IsType<JournalStep>(history.PeekUndo()).Undo());
        Assert.Equal(4, value);
    }
}
