using Poser.Application.Transforms;
using Poser.Domain.Identity;

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
