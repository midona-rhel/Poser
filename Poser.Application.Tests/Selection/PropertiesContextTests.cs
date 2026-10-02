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
    public void Pinning_from_a_bone_keeps_its_actor_not_a_private_bone_selection()
    {
        var (scene, first, _) = TwoActors();
        var bone = scene.Snapshot.Actors[0].Skeletons[0].Bones[0].Id;
        scene.Selection.Select(SelectionId.ForBone(bone));
        using var live = new PropertiesContext(scene);
        using var pinned = live.Pin();
        Assert.Equal(SelectionId.ForBone(bone), pinned.Selection.Primary);
        // A redraw can temporarily remove bones without removing the actor pin.
        scene.Refresh(new SceneSnapshot(2, [new(first, "First", [])], [], [], []));
        Assert.Equal(SelectionId.ForActor(first), pinned.Selection.Primary);
        Assert.True(pinned.IsAvailable);
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
    public void Pinned_group_targets_and_captured_callbacks_do_not_follow_workspace_selection()
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
    }

    [Fact]
    public void Bone_selection_from_either_pin_is_global_and_clear_keeps_the_pinned_subject()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        using var live = new PropertiesContext(scene);
        using var a = live.Pin();
        using var b = live.Pin();
        var bones = scene.Snapshot.Actors[0].Skeletons[0].Bones
            .Select(bone => SelectionId.ForBone(bone.Id)).ToArray();
        int changes = 0;
        scene.Selection.SelectionChanged += _ => changes++;

        a.WorkspaceSelection.Select(bones[0]);
        Assert.Equal(bones[0], live.Selection.Primary);
        Assert.Equal(bones[0], b.Selection.Primary);
        b.WorkspaceSelection.Toggle(bones[1]);
        Assert.Equal(bones, a.Selection.Selected);
        Assert.Equal(bones, scene.Selection.Selected);
        Assert.Equal(2, changes);

        b.WorkspaceSelection.Clear();
        Assert.Empty(live.Selection.Selected);
        Assert.Equal(SelectionId.ForActor(first), a.Selection.Primary);
        Assert.Equal(SelectionId.ForActor(first), b.Selection.Primary);
        scene.Selection.Select(SelectionId.ForActor(second));
        Assert.Equal(first, a.Selection.PrimaryActor);
        Assert.Equal(first, b.Selection.PrimaryActor);
        Assert.Equal(second, live.Selection.PrimaryActor);
    }

    [Fact]
    public void Workspace_range_and_sibling_selection_keep_their_shared_anchor()
    {
        var (scene, first, _) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        using var live = new PropertiesContext(scene);
        using var pinned = live.Pin();
        var bones = scene.Snapshot.Actors[0].Skeletons[0].Bones
            .Select(bone => SelectionId.ForBone(bone.Id)).ToArray();
        scene.Selection.Live.CompanionResolver = id => id == bones[0] ? bones[1] : null;
        pinned.WorkspaceSelection.Select(bones[0]);
        Assert.Equal(bones, pinned.Selection.Selected);
        Assert.Equal(bones[0], live.Selection.Anchor);
        pinned.WorkspaceSelection.SelectRange(live.Selection.Anchor!.Value, bones[1], bones);
        Assert.Equal(bones, pinned.Selection.Selected);
        Assert.Equal(bones[1], live.Selection.Anchor);
    }

    [Fact]
    public void Actor_pin_never_includes_another_actor_in_its_command_targets()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        using var live = new PropertiesContext(scene);
        using var pinned = live.Pin();
        scene.Selection.Add(SelectionId.ForActor(second));
        Assert.Equal(new[] { SelectionId.ForActor(first) }, pinned.Selection.Selected);
        scene.Selection.Select(SelectionId.ForBone(scene.Snapshot.Actors[1].Skeletons[0].Bones[0].Id));
        Assert.Equal(new[] { SelectionId.ForActor(first) }, pinned.Selection.Selected);
    }

    [Fact]
    public void Camera_pin_keeps_its_target_during_actor_selection()
    {
        var (scene, first, _) = TwoActors();
        var camera = CameraId.New();
        Assert.True(scene.TryRefresh(scene.Snapshot with
        {
            Revision = 2,
            Cameras =
            [
                new(CameraId.New(), "Main", CameraKind.Game, IsDefault: true),
                new(camera, "Camera", CameraKind.Free, IsLive: true),
            ],
        }).Accepted);
        scene.Selection.Select(SelectionId.ForCamera(camera));
        using var live = new PropertiesContext(scene);
        using var pinned = live.Pin();
        scene.Selection.Select(SelectionId.ForActor(first));
        Assert.Equal(SelectionId.ForCamera(camera), pinned.Selection.Primary);
        Assert.True(pinned.IsAvailable);
    }

    [Fact]
    public void Disposed_pin_no_longer_observes_scene_replacements()
    {
        var (scene, first, second) = TwoActors();
        scene.Selection.Select(SelectionId.ForActor(first));
        using var live = new PropertiesContext(scene);
        var pinned = live.Pin();
        pinned.Dispose();
        scene.Selection.Select(SelectionId.ForBone(scene.Snapshot.Actors[0].Skeletons[0].Bones[0].Id));
        Assert.Equal(SelectionId.ForActor(first), pinned.Selection.Primary);
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
