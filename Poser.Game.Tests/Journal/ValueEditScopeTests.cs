using Poser.Application.Diagnostics;
using Poser.Application.Transforms;

namespace Poser.Game.Tests.Journal;

public class ValueEditScopeTests
{
    [Fact]
    public void AnotherJournalOwnerCommitsEarlierValueEditBeforeItsOwnStep()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 0;
        journal.BeginEdit("field");
        journal.Set("value", "Value", () => value, v => value = v, 1);
        journal.EndEdit();
        var spawn = new SceneLifecyclePatch("Spawn", () => true, () => true);
        history.Append(spawn);
        Assert.Same(spawn, history.PeekUndo());
        history.CommitUndo(spawn);
        Assert.True(Assert.IsType<JournalStep>(history.PeekUndo()).Undo());
        Assert.Equal(0, value);
    }

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
            journal.Set("light", "Set intensity", () => value, v => value = v, i);
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
            journal.Set("name", "Rename", () => name, v => name = v, next);
            journal.EndEdit();
        }
        journal.Seal();
        bool visible = true;
        journal.Set("visible", "Hide", () => visible, v => visible = v, false);
        journal.Set("visible", "Show", () => visible, v => visible = v, true);
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

    [Fact]
    public void NetNoOpKeepsRedoAndAnEmptyFocusOrSelectionMakesNoEntry()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 0;
        journal.Set("value", "Value", () => value, v => value = v, 1);
        var prior = Assert.IsType<JournalStep>(history.PeekUndo());
        prior.Undo(); history.CommitUndo(prior);
        journal.BeginEdit("slider");
        journal.Set("value", "Value", () => value, v => value = v, 10);
        journal.Set("value", "Value", () => value, v => value = v, 0);
        journal.EndEdit(); journal.Seal();
        journal.BeginEdit("focused"); journal.EndEdit(); journal.Seal();
        Assert.False(history.CanUndo);
        Assert.Same(prior, history.PeekRedo());
    }

    [Fact]
    public void OneControlWritingSeveralValuesHasOneInverseAndOrderedRedo()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int a = 1, b = 2;
        var order = new List<string>();
        void A(int v) { a = v; order.Add("a"); }
        void B(int v) { b = v; order.Add("b"); }
        for (int i = 3; i <= 5; i++)
        {
            journal.BeginEdit("linked");
            journal.Set("a", "Linked values", () => a, A, i);
            int before = b;
            B(i * 2);
            journal.Record("Secondary", before, b, B);
            journal.EndEdit();
        }
        journal.Seal();
        order.Clear();
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal((1, 2), (a, b));
        Assert.Equal(new[] { "b", "a" }, order);
        history.CommitUndo(step);
        Assert.False(history.CanUndo);
        order.Clear();
        Assert.True(step.Redo());
        Assert.Equal((5, 10), (a, b));
        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void NewControlCommitsPreviousOneAndSuspendedChangesDoNotJournal()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        int value = 0;
        void Change(int next) => journal.Set("value", "Value", () => value, v => value = v, next);
        journal.BeginEdit("first"); Change(1); journal.EndEdit();
        journal.BeginEdit("second"); Change(2); journal.EndEdit();
        Assert.True(history.CanUndo);
        journal.Seal();
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(1, value);
        history.Clear();
        using (journal.Suspend())
        {
            journal.BeginEdit("suspended"); Change(3); journal.EndEdit();
        }
        journal.Seal();
        Assert.Equal(3, value);
        Assert.False(history.CanUndo);
    }
}
