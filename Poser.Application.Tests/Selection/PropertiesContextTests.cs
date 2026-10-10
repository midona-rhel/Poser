using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Selection;

public sealed class PropertiesContextTests
{
    [Fact]
    public void Removing_target_does_not_retarget_pin_and_restore_rebinds_same_lineage()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        using var live = new PropertiesContext(scene);
        using var pinned = live.Pin();
        scene.Refresh(new SceneSnapshot(2, [new(second, "Other", [])], [], [], []));
        scene.Selection.Select(SelectionId.ForActor(second));
        Assert.False(pinned.IsAvailable);
        Assert.Equal(first, pinned.Selection.PrimaryActor);
        var restored = new ActorId(first.LogicalId, first.Generation + 1);
        scene.Refresh(new SceneSnapshot(3, [new(restored, "Restored", []), new(second, "Other", [])], [], [], []));
        Assert.True(pinned.IsAvailable);
        Assert.Equal(restored, pinned.Selection.PrimaryActor);
        Assert.Equal(second, scene.Selection.PrimaryActor);
    }

    [Fact]
    public void Pinned_targets_and_captured_callbacks_do_not_follow_workspace_selection()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        scene.Selection.Add(SelectionId.ForActor(second));
        using var live = new PropertiesContext(scene);
        using var a = live.Pin();
        using var b = live.Pin();
        var captured = a.Selection.Selected.ToArray();
        a.WorkspaceSelection.Clear();
        scene.Selection.Select(SelectionId.ForActor(second));
        Assert.Equal(2, b.Selection.Selected.Count);
        Assert.Equal([SelectionId.ForActor(first), SelectionId.ForActor(second)], captured);
        Assert.Equal(captured, a.Selection.Selected);
        Assert.Equal(SelectionId.ForActor(second), a.WorkspaceSelection.Primary);

        // A single-actor pin never picks up another actor or its bones as command targets.
        scene.Selection.Select(SelectionId.ForActor(first));
        using var actorPin = live.Pin();
        scene.Selection.Add(SelectionId.ForActor(second));
        Assert.Equal(new[] { SelectionId.ForActor(first) }, actorPin.Selection.Selected);
        scene.Selection.Select(SelectionId.ForBone(scene.Snapshot.Actors[1].Skeletons[0].Bones[0].Id));
        Assert.Equal(new[] { SelectionId.ForActor(first) }, actorPin.Selection.Selected);
    }

    private static (SceneSession Scene, ActorId First, ActorId Second) TwoActors()
    {
        var first = new ActorId(Guid.NewGuid(), 1);
        var second = new ActorId(Guid.NewGuid(), 1);
        var scene = new SceneSession(new SelectionSession());
        scene.Refresh(new SceneSnapshot(1, [Actor(first, "First"), Actor(second, "Second")], [], [], []));
        return (scene, first, second);
    }

    private static ActorDescriptor Actor(ActorId actor, string name)
    {
        var skeleton = new SkeletonId(actor, PoseSlot.Character, 0);
        return new(actor, name, [new(skeleton,
        [
            new(new(skeleton, 0, 0, "j_kao"), "Head", null),
            new(new(skeleton, 0, 1, "j_ago"), "Jaw", null),
        ])]);
    }
}
