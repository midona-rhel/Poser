using Poser.Application.Transforms;
using static Poser.Application.Tests.Fixtures.JournalUndo;

namespace Poser.Application.Tests.Transforms;

public sealed class ValueJournalTests
{
    private sealed class Target
    {
        public float Opacity = 1f;
        public bool Alive = true;
    }

    private static void Set(ValueJournal journal, Target t, float value) =>
        journal.Set((t, "Opacity"), "Set opacity", () => t.Opacity, v => t.Opacity = v, value, () => t.Alive);

    [Fact]
    public void A_drag_is_one_step_that_undoes_to_the_value_before_the_drag()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var target = new Target();
        Set(journal, target, 1f); // setting the value it already holds is not a step
        Assert.False(history.CanUndo);

        journal.BeginEdit("opacity");
        Set(journal, target, 0.8f);
        Set(journal, target, 0.5f);
        Set(journal, target, 0.2f);
        journal.EndEdit();
        Assert.False(history.CanUndo);
        journal.Seal();

        Assert.Equal(0.2f, target.Opacity);
        Assert.True(Undo(history));
        Assert.Equal(1f, target.Opacity);
        Assert.False(history.CanUndo);
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
}
