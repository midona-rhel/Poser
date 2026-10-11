using NSubstitute;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using static Poser.Application.Tests.Fixtures.JournalUndo;

namespace Poser.Application.Tests.Transforms;

public sealed class GroupStepsTests
{
    private static SelectionId Actor() => SelectionId.ForActor(ActorId.New());

    [Fact]
    public void Creating_and_renaming_a_group_are_steps_that_put_the_whole_model_back()
    {
        var groups = new SceneGroups();
        var history = new EditHistory();
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
}
