using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Scene;

public sealed class GroupRemovalHistoryTests
{
    [Fact]
    public void Undoing_a_grouped_removal_restores_the_group_around_the_returned_entities()
    {
        var history = new EditHistory();
        var groups = new SceneGroups();
        var steps = new GroupSteps(groups, history, new ValueJournal(history));
        var a = SelectionId.ForLight(LightId.New());
        var b = SelectionId.ForLight(LightId.New());
        var loose = SelectionId.ForLight(LightId.New());
        groups.SyncRoot([loose]);
        var group = groups.Create("Lights", [a, b])!;
        var order = groups.RootOrder.ToArray();

        // A destroy brings each light back under a new id on undo.
        SelectionId? backA = null, backB = null;
        history.RetainLifecycleEntity(a, () => backA);
        history.RetainLifecycleEntity(b, () => backB);
        var nextA = SelectionId.ForLight(LightId.New());
        var nextB = SelectionId.ForLight(LightId.New());
        history.RecordLifecycleBatch("Remove entities", () =>
        {
            var before = steps.Capture();
            history.Append(new SceneLifecyclePatch("Destroy lights",
                () => { backA = nextA; backB = nextB; return true; },
                () => { backA = backB = null; return true; }));
            history.AppendBatchPrologue(steps.RemovalStructure(before));
        });
        groups.Prune(_ => false);
        Assert.Empty(groups.All);

        var entry = Assert.IsType<SceneLifecyclePatch>(history.PeekUndo());
        Assert.Equal("Destroy lights", entry.Description);
        Assert.True(entry.Undo());
        history.CommitUndo(entry);
        var restored = Assert.Single(groups.All);
        Assert.Equal(group.Id, restored.Id);
        Assert.Equal("Lights", restored.Name);
        Assert.Equal(new[] { nextA, nextB }, restored.Members);
        Assert.Equal(order, groups.RootOrder);

        // Redo removes again; the prune dissolves the same way, and a second
        // undo restores the same structure.
        Assert.True(entry.Redo());
        history.CommitRedo(entry);
        groups.Prune(id => id == loose);
        Assert.Empty(groups.All);
        Assert.True(entry.Undo());
        Assert.Equal(new[] { nextA, nextB }, Assert.Single(groups.All).Members);
    }

    [Fact]
    public void A_structure_prologue_alone_records_nothing()
    {
        var history = new EditHistory();
        var groups = new SceneGroups();
        var steps = new GroupSteps(groups, history, new ValueJournal(history));
        history.RecordLifecycleBatch("Remove entities", () =>
            history.AppendBatchPrologue(steps.RemovalStructure(steps.Capture())));
        Assert.False(history.CanUndo);
    }
}
