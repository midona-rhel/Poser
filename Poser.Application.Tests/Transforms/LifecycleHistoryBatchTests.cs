using Poser.Application.Transforms;

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
    public void Earlier_pending_value_edit_stays_outside_the_removal_entry()
    {
        var history = new TransformHistory();
        var values = new ValueJournal(history);
        int value = 0;
        values.Adjust("value", "Edit value", () => value, next => { value = next; return ValueWriteResult.Ok(); }, 2);
        history.RecordLifecycleBatch("Remove selection", () =>
            history.Append(new SceneLifecyclePatch("Remove", () => true, () => true)));
        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.True(entry.Undo());
        history.CommitUndo(entry);
        var earlier = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.Equal("Edit value", earlier.Description);
        Assert.True(earlier.Undo());
        Assert.Equal(0, value);
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
        Assert.False(entry.DropOnFailure!());
        available = true;
        Assert.True(entry.Undo());
        Assert.Equal(2, earlier);
        Assert.Equal(1, later);
        Assert.True(entry.Redo());
        Assert.True(entry.Undo());
        Assert.Equal(2, later);
    }

    [Fact]
    public void Permanent_refusal_discards_only_that_child_and_retains_siblings_for_redo()
    {
        var history = new TransformHistory();
        int restores = 0, removals = 0, failures = 0;
        history.RecordLifecycleBatch("Remove selection", () =>
        {
            history.Append(new SceneLifecyclePatch("Expired", () => { failures++; return false; }, () => throw new Exception())
            { FailureDetail = () => "Native incarnation expired", DropOnFailure = () => true });
            history.Append(new JournalStep("Survivor", () => { restores++; return true; }, () => { removals++; return true; }));
        });
        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.False(entry.Undo());
        Assert.Equal("Native incarnation expired", entry.FailureDetail!());
        Assert.False(entry.DropOnFailure!());
        Assert.True(entry.Undo());
        Assert.True(entry.Redo());
        Assert.Equal((1, 1, 1), (restores, removals, failures));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Journal_refusal_keeps_its_retain_or_second_refusal_drop_policy(bool retain)
    {
        var history = new TransformHistory();
        history.RecordLifecycleBatch("Release selection", () =>
            history.Append(new JournalStep("Release actor", () => false, () => true) { RetainOnFailure = retain }));
        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.False(entry.Undo());
        Assert.False(entry.DropOnFailure!());
        Assert.False(entry.Undo());
        Assert.Equal(!retain, entry.DropOnFailure!());
    }

    [Fact]
    public void Contextual_entry_ends_collection_and_keeps_its_context()
    {
        var history = new TransformHistory();
        var published = new List<HistoryEntry>();
        history.Appended += published.Add;
        var contextual = new JournalStep("Actor edit", () => true, () => true)
        { Context = new StepContext([], [], [], "pose.pose") };
        var later = new SceneLifecyclePatch("Later", () => true, () => true);
        history.RecordLifecycleBatch("Remove selection", () =>
        {
            history.Append(new SceneLifecyclePatch("First", () => true, () => true));
            history.Append(contextual);
            history.Append(later);
        });
        Assert.Equal(3, published.Count);
        Assert.Equal("First", published[0].Description);
        Assert.Same(contextual, published[1]);
        Assert.Same(later, published[2]);
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

    [Fact]
    public void Refused_command_without_recorded_removals_preserves_redo()
    {
        var history = new TransformHistory();
        var old = new JournalStep("Older", () => true, () => true);
        history.Append(old);
        history.CommitUndo(old);
        history.RecordLifecycleBatch("Nothing removed", () => { });
        Assert.False(history.CanUndo);
        Assert.Same(old, history.PeekRedo());
    }
}
