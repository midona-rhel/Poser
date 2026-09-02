using Poser.Application.Transforms;

namespace Poser.Game.Tests.Journal;

public sealed class ValueJournalTests
{
    private sealed class Target
    {
        public float Opacity = 1f;
        public bool Alive = true;
    }

    private static void Set(ValueJournal journal, Target t, float value) =>
        journal.Set((t, "Opacity"), "Set opacity", () => t.Opacity, v => t.Opacity = v, value, () => t.Alive);

    private static bool Undo(TransformHistory history)
    {
        var step = (JournalStep)history.PeekUndo()!;
        if (!step.Undo())
            return false;
        history.CommitUndo(step);
        return true;
    }

    [Fact]
    public void A_drag_is_one_step_that_undoes_to_the_value_before_the_drag()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();

        Set(journal, target, 0.8f);
        Set(journal, target, 0.5f);
        Set(journal, target, 0.2f);

        Assert.Equal(0.2f, target.Opacity);
        Assert.True(Undo(history));
        Assert.Equal(1f, target.Opacity);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Sealing_starts_a_new_step_on_the_same_key()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();

        Set(journal, target, 0.5f);
        journal.Seal();
        Set(journal, target, 0.2f);

        Assert.True(Undo(history));
        Assert.Equal(0.5f, target.Opacity);
        Assert.True(Undo(history));
        Assert.Equal(1f, target.Opacity);
    }

    [Fact]
    public void A_step_on_a_dead_target_undoes_as_a_no_op_so_the_steps_under_it_stay_reachable()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var first = new Target();
        var second = new Target();

        Set(journal, first, 0.5f);
        Set(journal, second, 0.3f);
        second.Alive = false;

        Assert.True(Undo(history));
        Assert.Equal(0.3f, second.Opacity);
        Assert.True(Undo(history));
        Assert.Equal(1f, first.Opacity);
    }

    [Fact]
    public void Setting_the_value_it_already_holds_is_not_a_step()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();

        Set(journal, target, 1f);

        Assert.False(history.CanUndo);
    }
}
