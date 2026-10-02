using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Selection;

public sealed class PropertiesContextTests
{
    [Fact]
    public void Reading_pinned_group_does_not_clear_the_workspace_active_group()
    {
        var groups = new SceneGroups();
        SelectionId[] first = [SelectionId.ForActor(ActorId.New()), SelectionId.ForActor(ActorId.New())];
        SelectionId[] second = [SelectionId.ForActor(ActorId.New()), SelectionId.ForActor(ActorId.New())];
        var pinned = groups.Create("Pinned", first)!;
        var active = groups.Create("Active", second)!;
        groups.ActiveGroupId = active.Id;
        Assert.Equal(pinned.Id, groups.MatchingSelection(first)?.Id);
        Assert.Equal(active.Id, groups.ActiveGroupId);
    }

    [Fact]
    public void Missing_pinned_bone_does_not_turn_into_an_actor_editor()
    {
        var (scene, first, _) = TwoActors();
        var bone = new BoneId(new(first, PoseSlot.Character, 0), 0, 1, "j_kao");
        scene.Selection.Select(SelectionId.ForBone(bone));
        using var live = new PropertiesContext(scene);
        using var pinned = live.Pin();
        scene.Refresh(new SceneSnapshot(2, [new(first, "First", [])], [], [], []));
        Assert.Equal(SelectionId.ForBone(bone), pinned.Selection.Primary);
        Assert.False(pinned.IsAvailable);
    }

    [Fact]
    public void Pinned_context_keeps_its_actor_when_workspace_selection_changes()
    {
        var (scene, first, second) = TwoActors();
        using var live = new PropertiesContext(scene);
        scene.Selection.Select(SelectionId.ForActor(first));
        using var pinned = live.Pin();
        scene.Selection.Select(SelectionId.ForActor(second));
        Assert.Equal(first, pinned.Selection.PrimaryActor);
        Assert.Equal(second, live.Selection.PrimaryActor);
        Assert.True(pinned.IsAvailable);
    }

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
    public void Two_pins_have_independent_local_selection_and_captured_callbacks()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        scene.Selection.Add(SelectionId.ForActor(second));
        using var live = new PropertiesContext(scene);
        using var a = live.Pin();
        using var b = live.Pin();
        var captured = a.Selection.Selected.ToArray();
        a.Selection.Clear();
        scene.Selection.Select(SelectionId.ForActor(second));
        Assert.Equal(2, b.Selection.Selected.Count);
        Assert.Equal([SelectionId.ForActor(first), SelectionId.ForActor(second)], captured);
        Assert.Empty(a.Selection.Selected);
    }

    [Fact]
    public void Disposed_pin_no_longer_observes_scene_replacements()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        using var live = new PropertiesContext(scene);
        var pinned = live.Pin();
        pinned.Dispose();
        var restored = new ActorId(first.LogicalId, first.Generation + 1);
        scene.Refresh(new SceneSnapshot(2, [new(restored, "Restored", [])], [], [], []));
        Assert.Equal(first, pinned.Selection.PrimaryActor);
        Assert.False(pinned.IsAvailable);
    }

    private static (SceneSession Scene, ActorId First, ActorId Second) TwoActors()
    {
        var first = new ActorId(Guid.NewGuid(), 1);
        var second = new ActorId(Guid.NewGuid(), 1);
        var scene = new SceneSession(new SelectionSession());
        scene.Refresh(new SceneSnapshot(1, [new(first, "First", []), new(second, "Second", [])], [], [], []));
        return (scene, first, second);
    }
}
