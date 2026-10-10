using Poser.Application.Diagnostics;
using Poser.Application.Transforms;

namespace Poser.Application.Tests.Transforms;

public class ValueEditScopeTests
{
    [Fact]
    public void SameControlAcrossFramesAppendsOnceAndRecorderSeesOnlyCommittedValues()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        using var recorder = new ActionRecorder(history);
        int value = 1;
        for (int i = 2; i <= 20; i++)
        {
            journal.BeginEdit("slider");
            journal.Set("light", "Set intensity", () => value, ValueWrites.Unchecked<int>(v => value = v), i);
            journal.EndEdit();
            Assert.False(history.CanUndo);
            Assert.Empty(recorder.Snapshot());
        }
        journal.CommitEdit("another control");
        Assert.False(history.CanUndo);
        journal.CommitEdit("slider");
        var record = Assert.Single(recorder.Snapshot());
        Assert.Equal(1, record.Before);
        Assert.Equal(20, record.After);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(1, value);
        Assert.True(step.Redo());
        Assert.Equal(20, value);
        journal.Seal();
        Assert.Single(recorder.Snapshot());
    }

    [Fact]
    public void TypedWordCommitsOnFocusLossAndSeparateClicksNeverFold()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        string name = "A";
        foreach (string next in new[] { "AB", "ABC" })
        {
            journal.BeginEdit("name");
            journal.Set("name", "Rename", () => name, ValueWrites.Unchecked<string>(v => name = v), next);
            journal.EndEdit();
        }
        journal.Seal();
        bool visible = true;
        journal.Set("visible", "Hide", () => visible, ValueWrites.Unchecked<bool>(v => visible = v), false);
        journal.Set("visible", "Show", () => visible, ValueWrites.Unchecked<bool>(v => visible = v), true);
        foreach (bool expected in new[] { false, true })
        {
            var step = Assert.IsType<JournalStep>(history.PeekUndo());
            Assert.True(step.Undo());
            Assert.Equal(expected, visible);
            history.CommitUndo(step);
        }
        Assert.True(Assert.IsType<JournalStep>(history.PeekUndo()).Undo());
        Assert.Equal("A", name);
    }
}
