using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Journal;

public sealed class GroupStepsTests
{
    private static SelectionId Actor() => SelectionId.ForActor(ActorId.New());

    private static bool Undo(TransformHistory history)
    {
        var step = (JournalStep)history.PeekUndo()!;
        if (!step.Undo())
            return false;
        history.CommitUndo(step);
        return true;
    }

    [Fact]
    public void Creating_and_renaming_a_group_are_steps_that_put_the_whole_model_back()
    {
        var groups = new SceneGroups();
        var history = new TransformHistory();
        var steps = new GroupSteps(groups, history, new ValueJournal(history));
        var members = new[] { Actor(), Actor() };

        var made = steps.Create("Pair", members)!;
        steps.Rename(made.Id, "Duo");

        Assert.Equal("Duo", groups.Find(made.Id)!.Name);
        Assert.True(Undo(history));
        Assert.Equal("Pair", groups.Find(made.Id)!.Name);
        Assert.True(Undo(history));
        Assert.Null(groups.Find(made.Id));
        Assert.Empty(groups.RootOrder.Where(slot => slot.IsGroup));
    }

    [Fact]
    public void A_composite_verb_is_one_step_and_the_gates_are_reapplied_on_the_way_back()
    {
        var groups = new SceneGroups();
        var history = new TransformHistory();
        var values = new ValueJournal(history);
        var steps = new GroupSteps(groups, history, values);
        int reapplied = 0;
        steps.ReapplyGates = () => reapplied++;
        var made = steps.Create("Pair", new[] { Actor(), Actor() })!;
        var target = new { Visible = true };

        steps.Run("Hide group", () =>
        {
            made.Hidden = true;
            // the routine's member writes journal on their own...
            values.Set((target, "Visible"), "Hide", () => true, _ => { }, false);
        });

        // ...and fold into the one step
        Assert.Equal("Hide group", history.UndoDescription);
        Assert.True(Undo(history));
        Assert.False(groups.Find(made.Id)!.Hidden);
        Assert.Equal(1, reapplied);
        Assert.Equal("Create group", history.UndoDescription);
    }
}
