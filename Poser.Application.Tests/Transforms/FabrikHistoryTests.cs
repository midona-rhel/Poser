using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Posing;

namespace Poser.Application.Tests.Transforms;

public sealed class FabrikHistoryTests
{
    [Fact]
    public void Endpoint_drag_commits_once_and_replays_the_exact_targets_and_seed()
    {
        var history = new TransformHistory();
        var journal = new ValueJournal(history);
        var entries = new List<HistoryEntry>();
        history.Appended += entries.Add;
        var point = new FabrikTarget(IkTargetMode.World, Vector3.One, Quaternion.Identity,
            new(4, 5, 6), Quaternion.Identity);
        var seed = new FabrikControl([
            new("root", 0, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity),
            new("tip", 0, Vector3.UnitX, Quaternion.Identity, Vector3.Zero, Quaternion.Identity)], point, point, 0);
        var before = IkChainConfig.DefaultsForChain(true) with
            { FabrikMode = FabrikControlMode.Bidirectional, Fabrik = seed };
        var current = before;
        for (int i = 1; i <= 10; i++)
        {
            journal.BeginEdit("root-position");
            journal.Adjust("chain", "Set IK", () => current,
                next => { current = next; return ValueWriteResult.Ok(); },
                current with { Fabrik = current.Fabrik! with
                    { Root = point with { Position = new Vector3(i, 2, 3) } } });
            journal.EndEdit();
        }
        Assert.Empty(entries);
        var after = current;
        journal.CommitEdit("root-position");
        var step = Assert.IsType<JournalStep>(Assert.Single(entries));
        Assert.True(step.Undo()); Assert.Same(before, current);
        Assert.True(step.Redo()); Assert.Same(after, current);
        Assert.Same(seed.Bones, current.Fabrik!.Bones);
        Assert.Equal(before.Fabrik!.Tip, current.Fabrik.Tip);
        Assert.Equal(point.AuthoredPosition, current.Fabrik.Root.AuthoredPosition);
    }
}
