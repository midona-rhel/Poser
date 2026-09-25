using System.Numerics;
using System.Reflection;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Domain.Posing;
using Poser.Game.Journal;
using Poser.Game.WorldObjects;
using Poser.Entities;
using Poser.Game.Scene;
using Poser.Services;

using Poser.Domain.Cameras;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The lifecycle seam's contract: an add or a remove is one entry in the
/// SAME history the transforms use, its two directions are exact inverses,
/// and the entity's IDENTITY survives a destroy/respawn pair so entries
/// stacked on one entity keep naming that entity rather than its corpse.
/// </summary>
public sealed class SceneLifecycleHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Camera_lifecycle_replay_preserves_the_surviving_view(bool lookThroughMain)
    {
        var world = new World();
        var main = world.Cameras.AddDefault();
        var survivor = world.Cameras.CreateCamera(CameraKind.Game)!;
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        camera.Position = new(1, 2, 3);
        camera.FoV = 0.8f;
        var view = lookThroughMain ? main : survivor;
        world.Cameras.SetLive(view);
        Assert.True(world.Undo()); // Undo spawn.
        Assert.Same(view, world.Cameras.LiveCamera);
        Assert.True(world.Redo());
        var restored = world.Cameras.Live.Single(c => c.Kind == CameraKind.Free);
        Assert.Same(view, world.Cameras.LiveCamera);
        Assert.Equal(new Vector3(1, 2, 3), restored.Position);
        Assert.Equal(0.8f, restored.FoV);

        var other = world.Lifecycle.CreateCamera(CameraKind.Game)!;
        world.Cameras.SetLive(restored);
        world.Lifecycle.DestroySelection(cameras: [restored, other]);
        Assert.Same(main, world.Cameras.LiveCamera); // Removing live still needs a fallback.
        world.Cameras.SetLive(view);
        Assert.True(world.Undo()); // Undo the whole removal batch.
        Assert.Same(view, world.Cameras.LiveCamera);
        Assert.Equal(4, world.Cameras.Live.Count);
        Assert.True(world.Redo());
        Assert.Same(view, world.Cameras.LiveCamera);
        Assert.Equal(2, world.Cameras.Live.Count);
    }

    [Fact]
    public void Camera_switch_redo_uses_the_restored_camera()
    {
        var world = new World();
        var values = new CameraSession(new ValueJournal(world.History), world.Cameras, null!, world.Lifecycle);
        var previous = world.Cameras.AddDefault();
        world.Cameras.SetLive(previous);
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        world.Cameras.SetLive(previous);
        values.SetLive(camera);
        world.Lifecycle.DestroyCamera(camera);
        Assert.True(world.Undo());
        var restored = Assert.Single(world.Cameras.Live.Where(c => !c.IsDefault));
        Assert.True(world.Undo());
        Assert.Same(previous, world.Cameras.LiveCamera);
        Assert.True(world.Redo());
        Assert.Same(restored, world.Cameras.LiveCamera);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prop_and_collider_transform_history_rebinds_after_removal(bool collider)
    {
        var world = new World();
        object entity = collider
            ? world.Lifecycle.SpawnOverlay(new OverlayNodeState { Kind = OverlayNodeKind.Collider })!
            : world.Lifecycle.SpawnProp(Apple)!;
        TransformTargetId Target(object instance) => instance switch
        {
            FakeProp prop => TransformTargetId.ForProp(prop.StableId),
            FakeOverlay overlay => TransformTargetId.ForCollider(overlay.StableId),
            _ => throw new InvalidOperationException(),
        };
        object Current() => collider ? Assert.Single(world.Overlays.Live) : Assert.Single(world.Props.Live);
        var oldTarget = Target(entity);
        var state = new TransformTargetState(oldTarget, PoseTransform.Identity, new BonePose(), false);
        world.History.Append(new TransformPatch("Move entity", [state], [state]));
        if (collider) world.Lifecycle.DestroyOverlay(entity);
        else world.Lifecycle.DestroyProp(entity);
        world.History.Reconcile(_ => false, _ => true);
        Assert.True(world.Undo());
        var newTarget = Target(Current());
        Assert.NotEqual(oldTarget, newTarget);
        var patch = Assert.IsType<TransformPatch>(world.History.PeekUndo());
        Assert.Equal(newTarget, Assert.Single(patch.Before).Target);
        world.History.CommitUndo(patch);
        Assert.True(world.Undo());
        world.History.Reconcile(_ => false, _ => true);
        Assert.True(world.Redo());
        var thirdTarget = Target(Current());
        Assert.NotEqual(newTarget, thirdTarget);
        Assert.Equal(thirdTarget, Assert.Single(Assert.IsType<TransformPatch>(world.History.PeekRedo()).After).Target);
    }

    [Fact]
    public void Camera_values_and_lock_survive_repeated_removal_and_creation()
    {
        var world = new World();
        var values = new CameraSession(new ValueJournal(world.History), world.Cameras, null!, world.Lifecycle);
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        Assert.True(values.SetZoom(camera, 7));
        values.SetLocked(camera, true);
        world.Lifecycle.DestroyCamera(camera);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(world.Undo());
            var restored = Assert.Single(world.Cameras.Live);
            Assert.NotSame(camera, restored);
            Assert.Equal(7, restored.Zoom);
            Assert.True(restored.IsLocked);
            Assert.True(world.Undo());
            Assert.False(restored.IsLocked);
            Assert.True(world.Undo());
            Assert.Equal(0, restored.Zoom);
            Assert.True(world.Undo());
            Assert.Empty(world.Cameras.Live);
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            restored = Assert.Single(world.Cameras.Live);
            Assert.Equal(7, restored.Zoom);
            Assert.True(restored.IsLocked);
            Assert.True(world.Redo());
            Assert.Empty(world.Cameras.Live);
        }
    }

    [Fact]
    public void Prop_values_and_model_survive_repeated_removal_and_creation()
    {
        var world = new World();
        var values = new PropSession(new ValueJournal(world.History), world.Lifecycle);
        var prop = (IPropHandle)world.Lifecycle.SpawnProp(Apple)!;
        var originalName = prop.Name;
        var changed = Apple with { Name = "Dyed" };
        values.SetName(prop, "Fruit");
        Assert.True(values.SetModel(prop, changed, out _));
        world.Lifecycle.DestroyProp(prop);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(world.Undo());
            var restored = (IPropHandle)Assert.Single(world.Props.Live);
            Assert.NotSame(prop, restored);
            Assert.Equal("Fruit", restored.Name);
            Assert.Equal(changed, restored.Model);
            Assert.True(world.Undo());
            Assert.Equal(Apple, restored.Model);
            Assert.True(world.Undo());
            Assert.Equal(originalName, restored.Name);
            Assert.True(world.Undo());
            Assert.Empty(world.Props.Live);
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            restored = (IPropHandle)Assert.Single(world.Props.Live);
            Assert.Equal("Fruit", restored.Name);
            Assert.Equal(changed, restored.Model);
            Assert.True(world.Redo());
            Assert.Empty(world.Props.Live);
        }
    }

    [Fact]
    public void Overlay_values_and_compound_size_survive_repeated_removal_and_creation()
    {
        var world = new World();
        var values = new OverlaySession(new ValueJournal(world.History), world.Lifecycle);
        var overlay = (IOverlayNode)world.Lifecycle.SpawnOverlay(new OverlayNodeState { Text = "Before", Scale = 2, Alpha = .5f })!;
        values.SetText(overlay, "After");
        values.ResetSize(overlay);
        world.Lifecycle.DestroyOverlay(overlay);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(world.Undo());
            var restored = (IOverlayNode)Assert.Single(world.Overlays.Live);
            Assert.NotSame(overlay, restored);
            Assert.Equal("After", restored.Text);
            Assert.Equal((1f, 1f), (restored.Scale, restored.Alpha));
            Assert.True(world.Undo());
            Assert.Equal((2f, .5f), (restored.Scale, restored.Alpha));
            Assert.True(world.Undo());
            Assert.Equal("Before", restored.Text);
            Assert.True(world.Undo());
            Assert.Empty(world.Overlays.Live);
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            restored = (IOverlayNode)Assert.Single(world.Overlays.Live);
            Assert.Equal("After", restored.Text);
            Assert.Equal((1f, 1f), (restored.Scale, restored.Alpha));
            Assert.True(world.Redo());
            Assert.Empty(world.Overlays.Live);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Light_edits_survive_release_and_repeated_restoration(bool borrowed)
    {
        var world = new World();
        var values = new LightSession(new ValueJournal(world.History), world.Lighting, world.Lifecycle);
        var light = borrowed
            ? world.Lifecycle.AcquireWorldLight(world.Lighting.Source)!
            : world.Lifecycle.SpawnLight(LightKind.Point)!;
        values.SetIntensity(light, 7);
        values.SetIsOn(light, false);
        world.Lifecycle.DestroyLight(light);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(world.Undo()); // release
            var restored = Assert.Single(world.Lighting.Lights);
            Assert.NotSame(light, restored);
            Assert.Equal(borrowed ? LightOwnership.World : LightOwnership.Spawned, restored.Ownership);
            Assert.Equal(7, restored.Intensity);
            Assert.False(restored.IsOn);
            Assert.True(world.Undo()); // off
            Assert.True(restored.IsOn);
            Assert.True(world.Undo()); // intensity
            Assert.Equal(1, restored.Intensity);
            Assert.True(world.Undo()); // acquire/spawn
            Assert.Empty(world.Lighting.Lights);
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            restored = Assert.Single(world.Lighting.Lights);
            Assert.Equal(7, restored.Intensity);
            Assert.False(restored.IsOn);
            Assert.True(world.Redo());
            Assert.Empty(world.Lighting.Lights);
        }
    }

    [Fact]
    public void Light_transform_history_survives_absence_and_rekeys_only_inside_history()
    {
        var world = new World();
        var light = world.Lifecycle.AcquireWorldLight(world.Lighting.Source)!;
        var oldTarget = world.Lighting.Target(light)!.Value;
        var state = new TransformTargetState(oldTarget, PoseTransform.Identity, new BonePose(), false);
        world.History.Append(new TransformPatch("Move light", [state], [state]));
        world.Lifecycle.DestroyLight(light);
        world.History.Reconcile(_ => false, _ => true);
        Assert.True(world.Undo());
        var restored = Assert.Single(world.Lighting.Lights);
        var newTarget = world.Lighting.Target(restored)!.Value;
        Assert.NotEqual(oldTarget, newTarget);
        Assert.Null(world.Lighting.Target(light)); // old public identity remains dead
        var patch = Assert.IsType<TransformPatch>(world.History.PeekUndo());
        Assert.Equal(newTarget, Assert.Single(patch.Before).Target);
        world.History.CommitUndo(patch);
        Assert.True(world.Undo()); // undo acquisition, removes second native copy
        world.History.Reconcile(_ => false, _ => true);
        Assert.True(world.Redo());
        var thirdTarget = world.Lighting.Target(Assert.Single(world.Lighting.Lights));
        Assert.NotEqual(newTarget, thirdTarget);
        Assert.Equal(thirdTarget, Assert.Single(Assert.IsType<TransformPatch>(world.History.PeekRedo()).After).Target);
    }

    [Fact]
    public void Released_light_cannot_reclaim_a_different_incarnation_at_the_same_address()
    {
        var world = new World();
        var light = world.Lifecycle.AcquireWorldLight(world.Lighting.Source)!;
        world.Lifecycle.DestroyLight(light);
        world.Lighting.Source = world.Lighting.Source with { Generation = 2 };
        Assert.False(world.Undo());
        Assert.Empty(world.Lighting.Lights);
        Assert.StartsWith("Release light", world.History.UndoDescription);
    }

    [Fact]
    public void Released_scenery_cannot_reclaim_an_address_reused_by_another_object()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        Assert.True(world.Lifecycle.ReleaseWorldObject(claim));
        world.WorldObjects.Place(address, UserPut);
        Assert.False(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(UserPut, world.WorldObjects.MapPlacement(address));
    }
    [Fact]
    public void Actor_removal_restores_latest_runtime_snapshot_without_replaying_the_clone_source()
    {
        var world = new World();
        int originalSpawns = 0;
        var actor = world.Lifecycle.SpawnActor("Clone", () =>
        {
            originalSpawns++;
            return world.Actors.Spawn("Source appearance");
        })!;
        var runtime = new ActorRuntimeState(null, new Poser.Application.Posing.ActorPropertiesSnapshot(0,
            new Poser.Application.Integration.ActorAppearanceSnapshot("authored appearance", null, null, null, null),
            PresentationOverrides.None, null, null), null, null, []);
        var authored = new ActorState(MapStood, false, null) { Runtime = runtime };
        world.Actors.Edit(actor, authored);
        world.Lifecycle.DespawnActor(actor);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(world.Undo());
            var restored = Assert.Single(world.Actors.Live);
            Assert.Equal(authored, world.Actors.StateOf(restored));
            Assert.True(world.Redo());
            Assert.Empty(world.Actors.Live);
        }
        Assert.Equal(1, originalSpawns);
    }

    [Fact]
    public void Camera_removal_restores_origin_lock_tracking_and_optics()
    {
        var world = new World();
        world.Cameras.SpawnPosition = new(9, 8, 7);
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        camera.Position = Vector3.Zero;
        camera.IsLocked = true;
        camera.IsTracking = true;
        camera.TrackingMode = CameraTrackingMode.Pan;
        camera.TargetOffset = new(1, 2, 3);
        camera.IsTargetLocked = true;
        camera.FoV = 0.45f;
        camera.Orthographic = true;
        camera.OrthographicZoom = 7;
        world.Lifecycle.DestroyCamera(camera);
        Assert.True(world.Undo());
        var restored = Assert.Single(world.Cameras.Live);
        Assert.Equal(Vector3.Zero, restored.Position);
        Assert.True(restored.IsLocked);
        Assert.True(restored.IsTracking);
        Assert.True(restored.IsTargetLocked);
        Assert.Equal(CameraTrackingMode.Pan, restored.TrackingMode);
        Assert.Equal(new Vector3(1, 2, 3), restored.TargetOffset);
        Assert.Equal(0.45f, restored.FoV);
        Assert.True(restored.Orthographic);
        Assert.Equal(7, restored.OrthographicZoom);
    }

    [Fact]
    public void Light_removal_restores_attachment_and_emission_settings()
    {
        var world = new World();
        var bone = DispatchProxy.Create<IBone, BoneProxy>();
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        light.AttachedBone = bone;
        light.Intensity = 4;
        light.Color = new(.2f, .3f, .4f);
        light.SpotAngle = .7f;
        light.CastsCharacterShadow = true;
        world.Lifecycle.DestroyLight(light);
        Assert.True(world.Undo());
        var restored = Assert.Single(world.Lighting.Lights);
        Assert.Same(bone, restored.AttachedBone);
        Assert.Equal(4, restored.Intensity);
        Assert.Equal(new Vector3(.2f, .3f, .4f), restored.Color);
        Assert.Equal(.7f, restored.SpotAngle);
        Assert.True(restored.CastsCharacterShadow);
    }

    private class BoneProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "get_Skeleton" ? DispatchProxy.Create<ISkeleton, SkeletonProxy>() : null;
    }

    private class SkeletonProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "get_IsValid" ? true : null;
    }

    [Fact]
    public void Overlay_copies_advance_names_in_a_batch_and_redo_preserves_name_and_offset()
    {
        var world = new World();
        var one = world.Lifecycle.SpawnOverlay(new OverlayNodeState { Name = "Key 1" })!;
        var two = world.Lifecycle.CloneOverlay(one)!;
        var three = world.Lifecycle.CloneOverlay(two)!;
        Assert.Equal("Key 3", world.Overlays.Read(three).Name);
        world.Lifecycle.DestroyOverlay(two);
        var four = world.Lifecycle.CloneOverlay(one)!;
        Assert.Equal("Key 4", world.Overlays.Read(four).Name);
        var state = world.Overlays.Read(four);
        Assert.Equal(world.Overlays.Read(one).Position + new Vector2(24f), state.Position);
        Assert.True(world.Undo());
        Assert.True(world.Redo());
        Assert.Contains(world.Overlays.Live, x => world.Overlays.Read(x) == state);
    }

    [Fact]
    public void Duplicate_actor_advances_source_series_and_redo_keeps_the_authored_name()
    {
        var world = new World();
        var one = world.Lifecycle.SpawnActor("Add", () => world.Actors.Spawn("Native"))!;
        Assert.Equal("Actor 1", one.Name);
        var two = world.Lifecycle.SpawnActor("Copy", () => world.Actors.Spawn("Native"), source: one)!;
        var three = world.Lifecycle.SpawnActorWithPose("Copy posed", () => world.Actors.Spawn("Native"), two)!;
        Assert.Equal("Actor 3", three.Name);
        world.Lifecycle.DespawnActor(two);
        var four = world.Lifecycle.SpawnActor("Copy original", () => world.Actors.Spawn("Native"), source: one)!;
        Assert.Equal("Actor 4", four.Name);
        four.Name = "Lead 7";
        Assert.True(world.Undo());
        Assert.True(world.Redo());
        Assert.Contains(world.Actors.Live, x => x.Name == "Lead 7");
    }

    [Fact]
    public void Duplicate_prop_advances_past_a_deleted_middle_name_and_restores_the_copy_name()
    {
        var world = new World();
        var one = world.Lifecycle.SpawnProp(Apple)!;
        world.Props.Apply(one, world.Props.Read(one) with { Name = "Key 1" });
        var two = world.Lifecycle.CloneProp(one)!;
        Assert.Equal("Key 2", world.Props.Read(two).Name);
        var three = world.Lifecycle.CloneProp(two)!;
        Assert.Equal("Key 3", world.Props.Read(three).Name);
        world.Lifecycle.DestroyProp(two);
        var four = world.Lifecycle.CloneProp(one)!;
        Assert.Equal("Key 4", world.Props.Read(four).Name);
        Assert.True(world.Undo());
        Assert.True(world.Redo());
        Assert.Contains(world.Props.Live, x => world.Props.Read(x).Name == "Key 4");
    }

    [Fact]
    public void Add_remove_undo_redo_preserves_latest_state_and_the_entity_slot()
    {
        var world = new World();
        var original = world.Lifecycle.SpawnProp(Apple)!;
        var moved = Transform.Identity;
        moved.Position = new Vector3(4, 5, 6);
        world.Props.Apply(original, new PropState("Fruit", Apple, moved, false));

        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
        Assert.True(world.Redo());

        var restored = Assert.Single(world.Props.Live);
        Assert.NotSame(original, restored);
        Assert.Equal("Fruit", world.Props.Read(restored).Name);
        Assert.Equal(moved, world.Props.Read(restored).Transform);
        Assert.False(world.Props.Read(restored).Visible);

        world.Lifecycle.DestroyProp(restored);
        Assert.True(world.Undo());
        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
    }

    [Fact]
    public void Refusals_do_not_add_history_or_discard_the_previous_entry()
    {
        var world = new World
        {
            Lighting = { RefuseSpawn = true },
            Props = { RefuseSpawn = true },
        };

        Assert.Null(world.Lifecycle.SpawnLight(LightKind.Spot));
        Assert.Null(world.Lifecycle.SpawnProp(Apple));
        Assert.False(world.History.CanUndo);

        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        Assert.Equal("Add actor", world.History.UndoDescription);
        world.Actors.RefuseDestroy = true;
        world.Lifecycle.DespawnActor(actor);
        Assert.Single(world.Actors.Live);
        Assert.Equal("Add actor", world.History.UndoDescription);
    }

    [Fact]
    public void Selection_removal_restores_actors_and_other_entities_in_one_entry()
    {
        var world = new World();
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        var prop = world.Lifecycle.SpawnProp(Apple)!;
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        world.History.RecordLifecycleBatch("Remove selection", () =>
        {
            Assert.True(world.Lifecycle.DespawnActor(actor));
            world.Lifecycle.DestroyProp(prop);
            world.Lifecycle.DestroyLight(light);
            world.Lifecycle.DestroyCamera(camera);
        });
        Assert.Empty(world.Actors.Live);
        Assert.Empty(world.Props.Live);
        Assert.Empty(world.Lighting.Live);
        Assert.Empty(world.Cameras.Live);
        Assert.Equal("Remove selection", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Single(world.Actors.Live);
        Assert.Single(world.Lighting.Live);
        Assert.Single(world.Props.Live);
        Assert.Single(world.Cameras.Live);
        Assert.Equal("Add actor", world.History.UndoDescription);
        Assert.True(world.Redo());
        Assert.Empty(world.Actors.Live);
        Assert.Empty(world.Props.Live);
        Assert.Empty(world.Lighting.Live);
        Assert.Empty(world.Cameras.Live);
    }

    [Fact]
    public void Refused_actor_removal_does_not_lose_successful_sibling_inverses()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        var prop = world.Lifecycle.SpawnProp(Apple)!;
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        world.Actors.RefuseDestroy = true;
        Assert.Equal(2, world.Lifecycle.DestroySelection(
            actors: [actor], props: [prop], lights: [light]));
        Assert.Same(actor, Assert.Single(world.Actors.Live));
        Assert.Empty(world.Props.Live);
        Assert.Empty(world.Lighting.Live);
        Assert.True(world.Undo());
        Assert.Same(actor, Assert.Single(world.Actors.Live));
        Assert.Single(world.Props.Live);
        Assert.Single(world.Lighting.Live);
        Assert.NotEqual("Remove selection", world.History.UndoDescription);
        Assert.True(world.Redo());
        Assert.Same(actor, Assert.Single(world.Actors.Live));
        Assert.Empty(world.Props.Live);
        Assert.Empty(world.Lighting.Live);
    }

    [Fact]
    public void World_adoption_undo_releases_and_redo_reclaims_the_same_incarnation()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(claim, world.WorldObjects.Read(claim) with
        {
            Placement = UserPut,
            Visible = false,
        });

        Assert.True(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));
        Assert.True(world.Redo());
        var restored = Assert.Single(world.WorldObjects.Live);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
        Assert.False(world.WorldObjects.Read(restored).Visible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Releasing_borrowed_world_object_then_undo_reclaims_authored_state(bool isVfx)
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1800, MapStood, isVfx);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(claim, world.WorldObjects.Read(claim) with
        {
            Placement = UserPut,
            Visible = false,
        });

        Assert.True(world.Lifecycle.ReleaseWorldObject(claim));
        Assert.Empty(world.WorldObjects.Live);
        Assert.True(world.Undo());

        var restored = Assert.Single(world.WorldObjects.Live);
        Assert.Equal(isVfx, Assert.IsType<FakeWorldObject>(restored).IsVfx);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
        Assert.False(world.WorldObjects.Read(restored).Visible);
        Assert.Equal(2, world.WorldObjects.AdoptCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Borrowed_world_objects_can_be_acquired_but_replacement_identity_is_refused(bool isVfx)
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x2000, MapStood, isVfx);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        Assert.NotNull(claim); // Initial BG/VFX borrowing still works.
        Assert.Equal(isVfx, Assert.IsType<FakeWorldObject>(claim).IsVfx);
        Assert.Equal(1, world.WorldObjects.AdoptCalls);

        world.Lifecycle.ReleaseWorldObject(claim);
        var replacementPlacement = new Transform(new System.Numerics.Vector3(9, 8, 7),
            System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One);
        world.WorldObjects.Place(address, replacementPlacement, isVfx);
        var refused = Assert.IsType<SceneLifecyclePatch>(world.History.PeekUndo());

        Assert.False(refused.Undo());
        Assert.Equal(1, world.WorldObjects.AdoptCalls); // No second adoption after release.
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(replacementPlacement, world.WorldObjects.MapPlacement(address));
        Assert.Contains("identity", refused.FailureDetail!(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_borrowed_candidate_is_skipped_and_older_history_continues()
    {
        var world = new World();
        world.History.Append(new JournalStep("Earlier edit", () => true, () => true));
        var address = world.WorldObjects.Place(0x2400, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        Assert.True(world.Lifecycle.ReleaseWorldObject(claim));
        world.WorldObjects.Remove(address);

        Assert.False(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(1, world.WorldObjects.AdoptCalls);
        Assert.Contains("no longer in the current world graph", Assert.Single(world.Notices), StringComparison.OrdinalIgnoreCase);
        Assert.True(world.Undo());
        Assert.False(world.History.CanUndo);
    }

    [Fact]
    public void Owned_world_object_restore_still_spawns_and_restores()
    {
        var world = new World();
        var spawned = world.Lifecycle.SpawnWorldObject("bg/owned.mdl", UserPut, true)!;
        Assert.True(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.True(world.Redo());
        var restored = Assert.Single(world.WorldObjects.Live);
        Assert.NotSame(spawned, restored);
        Assert.Equal("bg/owned.mdl", world.WorldObjects.Read(restored).Path);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
        Assert.True(world.WorldObjects.Read(restored).Visible);
    }

    [Fact]
    public void Released_borrowed_edit_is_invalidated_and_failed_restore_does_not_block_older_history()
    {
        var world = new World();
        world.History.Append(new JournalStep("Earlier unrelated edit", () => true, () => true));
        var address = world.WorldObjects.Place(0x3000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        var target = world.WorldObjects.Target(claim);
        var state = new TransformTargetState(target, PoseTransform.Identity, new BonePose(), false);
        world.History.Append(new TransformPatch("Move borrowed BG", [state], [state]));
        Assert.True(world.Lifecycle.ReleaseWorldObject(claim));
        world.WorldObjects.Place(address, MapStood); // Same address, new incarnation.

        Assert.Equal("Remove world object", world.History.UndoDescription);
        Assert.False(world.Undo()); // Fails safely and drops the permanently un-restorable claim.
        Assert.Equal("Earlier unrelated edit", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.False(world.History.CanUndo);
        Assert.Equal(1, world.WorldObjects.AdoptCalls);
    }

    [Fact]
    public void Failed_live_release_keeps_the_borrowed_object_transform_history()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x3500, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        var target = world.WorldObjects.Target(claim);
        var state = new TransformTargetState(target, PoseTransform.Identity, new BonePose(), false);
        world.History.Append(new TransformPatch("Move borrowed BG", [state], [state]));
        world.WorldObjects.RefuseRelease = true;

        Assert.False(world.Lifecycle.ReleaseWorldObject(claim));

        Assert.Equal("Move borrowed BG", world.History.UndoDescription);
        Assert.Single(world.WorldObjects.Live);
    }

    [Fact]
    public void Mixed_release_group_refuses_before_restoring_owned_members()
    {
        var world = new World();
        world.History.Append(new JournalStep("Earlier edit", () => true, () => true));
        var borrowedAddress = world.WorldObjects.Place(0x4000, MapStood);
        _ = world.Lifecycle.AdoptWorldObject(borrowedAddress);
        _ = world.Lifecycle.SpawnWorldObject("bg/owned.mdl", UserPut, true);
        Assert.True(world.Lifecycle.ReleaseAllWorldObjects());
        world.WorldObjects.Place(borrowedAddress, MapStood); // Reused address invalidates borrowed history.
        var group = Assert.IsType<SceneLifecyclePatch>(world.History.PeekUndo());
        int spawnCallsAfterRelease = world.WorldObjects.SpawnCalls;

        Assert.False(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(spawnCallsAfterRelease, world.WorldObjects.SpawnCalls);
        Assert.Equal(1, world.WorldObjects.AdoptCalls);
        Assert.Contains("identity", group.FailureDetail!(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Earlier edit", world.History.UndoDescription);
    }

    [Fact]
    public void Owned_only_release_group_still_restores_all_members()
    {
        var world = new World();
        _ = world.Lifecycle.SpawnWorldObject("bg/owned-a.mdl", UserPut, true);
        _ = world.Lifecycle.SpawnWorldObject("bg/owned-b.mdl", MapStood, false);
        Assert.True(world.Lifecycle.ReleaseAllWorldObjects());
        int spawnCallsAfterRelease = world.WorldObjects.SpawnCalls;

        Assert.True(world.Undo());
        Assert.Equal(2, world.WorldObjects.Live.Count);
        Assert.Equal(spawnCallsAfterRelease + 2, world.WorldObjects.SpawnCalls);
        Assert.Contains(world.WorldObjects.Live, item => world.WorldObjects.Read(item).Path == "bg/owned-a.mdl");
        Assert.Contains(world.WorldObjects.Live, item => world.WorldObjects.Read(item).Path == "bg/owned-b.mdl");
    }

    [Fact]
    public void Release_all_records_successes_before_a_later_live_claim_refuses()
    {
        var world = new World();
        world.History.Append(new JournalStep("Earlier edit", () => true, () => true));
        _ = world.Lifecycle.SpawnWorldObject("bg/owned.mdl", UserPut, true);
        var borrowedAddress = world.WorldObjects.Place(0x5000, MapStood);
        _ = world.Lifecycle.AdoptWorldObject(borrowedAddress);
        world.WorldObjects.RefuseReleaseAddresses.Add(borrowedAddress);

        Assert.False(world.Lifecycle.ReleaseAllWorldObjects());

        Assert.Equal("Remove world object", world.History.UndoDescription);
        Assert.Equal("bg/fake.mdl", world.WorldObjects.Read(Assert.Single(world.WorldObjects.Live)).Path);
        Assert.True(world.Undo()); // The successful owned release has its own restore entry.
        Assert.Contains(world.WorldObjects.Live,
            item => world.WorldObjects.Read(item).Path == "bg/owned.mdl");
        Assert.Equal(2, world.WorldObjects.Live.Count);
        world.WorldObjects.RefuseReleaseAddresses.Clear();
        Assert.True(world.Undo()); // The still-live borrowed claim remains undoable.
        Assert.True(world.Undo()); // The owned acquisition remains undoable too.
        Assert.True(world.Undo());
        Assert.False(world.History.CanUndo);
    }

    [Fact]
    public void Group_redo_retry_skips_members_already_released_before_a_later_refusal()
    {
        var world = new World();
        _ = world.Lifecycle.SpawnWorldObject("bg/owned-a.mdl", UserPut, true);
        _ = world.Lifecycle.SpawnWorldObject("bg/owned-b.mdl", MapStood, true);
        world.Lifecycle.ReleaseAllWorldObjects();

        Assert.True(world.Undo());
        world.WorldObjects.RefuseReleasePaths.Add("bg/owned-b.mdl");
        Assert.False(world.Redo()); // A is released before B refuses.
        Assert.Single(world.WorldObjects.Live);
        Assert.Equal("bg/owned-b.mdl", world.WorldObjects.Read(Assert.Single(world.WorldObjects.Live)).Path);

        world.WorldObjects.RefuseReleasePaths.Clear();
        Assert.True(world.Redo()); // A is already complete; retry only releases B.
        Assert.Empty(world.WorldObjects.Live);
        Assert.True(world.Undo());
        Assert.Equal(2, world.WorldObjects.Live.Count);
    }

    [Fact]
    public void History_keeps_lifecycle_entries_ordered_and_clears_slots_when_disabled()
    {
        var world = new World();
        world.History.Append(new TransformPatch("edit", [], []));
        world.Lifecycle.SpawnLight(LightKind.Spot);
        Assert.Equal("Add spot light", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal("edit", world.History.UndoDescription);
        world.History.Reconcile(static _ => false, _ => true);
        Assert.True(world.History.CanUndo);

        var disabled = new World(capacity: 0);
        Assert.NotNull(disabled.Lifecycle.SpawnLight(LightKind.Spot));
        Assert.False(disabled.History.CanUndo);
        Assert.Equal(0, SlotCount(disabled.Lifecycle, "_lightOwner"));
    }

    [Fact]
    public void Add_remove_undo_redo_covers_actor_and_camera_identity()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;

        Assert.True(world.Undo());
        Assert.Empty(world.Cameras.Live);
        Assert.True(world.Undo());
        Assert.Empty(world.Actors.Live);

        Assert.True(world.Redo());
        Assert.True(world.Redo());
        Assert.Single(world.Actors.Live);
        Assert.Single(world.Cameras.Live);
        Assert.NotSame(actor, world.Actors.Live[0]);
        Assert.NotSame(camera, world.Cameras.Live[0]);
    }

    private static ActorState Posed(Vector3 position, bool visible)
    {
        var placement = Transform.Identity;
        placement.Position = position;
        return new ActorState(placement, visible, new Poser.Files.PoseFile());
    }

    private static readonly PropModel Apple =
        new("Apple", 9001, 249, 1, "The default prop");

    private static readonly Transform MapStood = new(
        new Vector3(4f, 0f, 8f), Quaternion.Identity, Vector3.One);

    private static readonly Transform UserPut = new(
        new Vector3(40f, 6f, 80f), Quaternion.Identity, new Vector3(2f, 2f, 2f));

    private static int SlotCount(SceneLifecycleHistory lifecycle, string field)
    {
        var owner = typeof(SceneLifecycleHistory)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lifecycle)!;
        return (int)owner.GetType().GetProperty("Count")!.GetValue(owner)!;
    }

    // ── harness ──────────────────────────────────────────────────────────

    /// <summary>The seam over fake services, plus the undo/redo dispatch the
    /// gesture service performs on a lifecycle entry: run the direction, and
    /// move the entry between stacks only if it landed.</summary>
    private sealed class World
    {
        public TransformHistory History { get; }
        public FakeLighting Lighting { get; } = new();
        public FakeCameras Cameras { get; } = new();
        public FakeActors Actors { get; } = new();
        public FakeProps Props { get; } = new();
        public FakeOverlays Overlays { get; } = new();
        public FakeWorldObjects WorldObjects { get; } = new();
        public SceneLifecycleHistory Lifecycle { get; }
        public List<string> Notices { get; } = new();

        /// <param name="capacity">Undo depth; below 1 is undo switched off.
        /// </param>
        public World(int capacity = TransformHistory.DefaultCapacity)
        {
            History = new TransformHistory(() => capacity);
            Lifecycle = new SceneLifecycleHistory(
                History, Lighting, Cameras, Actors, Props, Overlays,
                WorldObjects, Lighting.Target,
                worldObject => worldObject is FakeWorldObject fake
                    ? TransformTargetId.ForWorldObject(fake.Id) : null,
                prop => prop is FakeProp { IsValid: true } fake ? TransformTargetId.ForProp(fake.StableId) : null,
                overlay => overlay is FakeOverlay { IsValid: true, Kind: OverlayNodeKind.Collider } fake
                    ? TransformTargetId.ForCollider(fake.StableId) : null);
        }

        public bool Undo()
        {
            var entry = History.PeekUndo()!;
            if (!(entry switch { SceneLifecyclePatch p => p.Undo(), JournalStep p => p.Undo(), _ => false }))
            {
                if (entry is SceneLifecyclePatch { DropOnFailure: { } shouldDrop } patch && shouldDrop())
                {
                    History.Drop(entry);
                    Notices.Add(patch.FailureDetail?.Invoke() ?? "Lifecycle restore refused.");
                }
                return false;
            }
            History.CommitUndo(entry);
            return true;
        }

        public bool Redo()
        {
            var entry = History.PeekRedo()!;
            if (!(entry switch { SceneLifecyclePatch p => p.Redo(), JournalStep p => p.Redo(), _ => false }))
            {
                if (entry is SceneLifecyclePatch { DropOnFailure: { } shouldDrop } patch && shouldDrop())
                {
                    History.Drop(entry);
                    Notices.Add(patch.FailureDetail?.Invoke() ?? "Lifecycle restore refused.");
                }
                return false;
            }
            History.CommitRedo(entry);
            return true;
        }
    }

    private sealed class FakeLighting : ILightingService
    {
        private readonly List<ILight> _lights = new();
        private readonly Dictionary<ILight, LightId> _ids = new();
        private readonly Dictionary<ILight, WorldLightCandidate> _sources = new();
        public WorldLightCandidate Source = new(0x1234, 0, Generation: 1);
        public TransformTargetId? Target(ILight light)
        {
            if (!light.IsValid || !_lights.Contains(light)) return null;
            if (!_ids.TryGetValue(light, out var id)) _ids[light] = id = LightId.New();
            return TransformTargetId.ForLight(id);
        }

        public bool RefuseSpawn { get; set; }
        public IReadOnlyList<ILight> Live => _lights;

        public bool IsAvailable => true;
        public IReadOnlyList<ILight> Lights => _lights;
        public IReadOnlyList<GoboEntry> Gobos => Array.Empty<GoboEntry>();
        public void Dispose() { }

        public ILight? SpawnLight(LightKind kind)
        {
            if (RefuseSpawn)
                return null;
            var light = new FakeLight { Kind = kind };
            _lights.Add(light);
            return light;
        }

        public ILight? CloneLight(ILight source) => SpawnLight(source.Kind);

        public void DestroyLight(ILight light)
        {
            _lights.Remove(light);
            ((FakeLight)light).IsValid = false;
        }

        /// <summary>The light leaves without this seam's knowledge — a scene
        /// import, or the game.</summary>
        public void VanishWithoutNotice(ILight light) => DestroyLight(light);

        public ILight AddBorrowed()
        {
            var light = new FakeLight { Ownership = LightOwnership.World };
            _lights.Add(light);
            return light;
        }

        public void DestroyAllLights() => _lights.Clear();

        public bool IsSpawnedLight(ILight light) =>
            light.Ownership == LightOwnership.Spawned;

        public void ReleaseLight(ILight light) => _lights.Remove(light);
        public bool ApplyGobo(ILight light, GoboEntry gobo) => false;
        public void ClearGobo(ILight light) { }
        public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates() =>
            Array.Empty<WorldLightCandidate>();
        public ILight? CaptureWorldLight(WorldLightCandidate candidate)
        {
            if (candidate != Source || _lights.Any(l => l.Ownership == LightOwnership.World)) return null;
            var light = AddBorrowed();
            _sources[light] = candidate;
            return light;
        }
        public WorldLightCandidate? GetWorldSource(ILight light) =>
            _sources.TryGetValue(light, out var source) ? source : null;
    }

    private sealed class FakeLight : ILight
    {
        public bool IsValid { get; set; } = true;
        public string Name { get; set; } = "Light";
        public LightKind Kind { get; set; }
        public bool IsOn { get; set; } = true;
        public Transform Transform { get; set; } = Transform.Identity;
        public Vector3 Color { get; set; } = Vector3.One;
        public float Intensity { get; set; } = 1f;
        public float Range { get; set; } = 1f;
        public float Falloff { get; set; }
        public LightFalloffType FalloffType { get; set; }
        public float SpotAngle { get; set; }
        public float FalloffAngle { get; set; }
        public Vector2 AreaAngle { get; set; }
        public bool HasReflection { get; set; }
        public bool CastsDynamicShadows { get; set; }
        public bool CastsCharacterShadow { get; set; }
        public bool CastsObjectShadow { get; set; }
        public float CharacterShadowRange { get; set; }
        public float ShadowPlaneNear { get; set; }
        public float ShadowPlaneFar { get; set; }
        public LightOwnership Ownership { get; set; } = LightOwnership.Spawned;
        public string? GoboPath => null;
        public IBone? AttachedBone { get; set; }
    }

    private sealed class FakeCameras : IVirtualCameraService
    {
        public Vector3 SpawnPosition { get; set; }
        public bool SuppressFlightKeys { get; set; }
        public bool FlightActive => false;
        private readonly List<IVirtualCamera> _cameras = new();

        public IReadOnlyList<IVirtualCamera> Live => _cameras;

        public bool IsAvailable => true;
        public IReadOnlyList<IVirtualCamera> Cameras => _cameras;
        public IVirtualCamera? LiveCamera { get; private set; }

        public FreeCameraSpeedNotice? SpeedNotice => null;
        public void Dispose() { }

        public IVirtualCamera? CreateCamera(CameraKind kind, bool makeLive = true)
        {
            var camera = new FakeCamera(kind) { Position = SpawnPosition };
            _cameras.Add(camera);
            if (makeLive) SetLive(camera);
            return camera;
        }

        public IVirtualCamera? CloneCamera(IVirtualCamera source) =>
            CreateCamera(source.Kind);

        public IVirtualCamera AddDefault()
        {
            var camera = new FakeCamera(CameraKind.Game) { IsDefault = true };
            _cameras.Add(camera);
            return camera;
        }

        public void DestroyCamera(IVirtualCamera camera)
        {
            _cameras.Remove(camera);
            if (ReferenceEquals(LiveCamera, camera))
            {
                ((FakeCamera)camera).IsLive = false;
                LiveCamera = null;
                if (_cameras.FirstOrDefault(candidate => candidate.IsDefault) is { } fallback)
                    SetLive(fallback);
            }
            ((FakeCamera)camera).IsValid = false;
        }

        public void DestroyAllCameras() => _cameras.Clear();
        public void SetLive(IVirtualCamera camera)
        {
            if (LiveCamera is FakeCamera previous) previous.IsLive = false;
            LiveCamera = camera;
            ((FakeCamera)camera).IsLive = true;
        }
        public bool SetTargetActor(
            IVirtualCamera camera, IActor actor, ActorId actorId,
            string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
        // Camera framing is outside lifecycle-history coverage.
        public CameraCenterResult CenterOnActor(IActor actor) =>
            CameraCenterResult.Refused("not available in lifecycle fake");
        public CameraCenterResult CenterOnBone(IBone bone) =>
            CameraCenterResult.Refused("not available in lifecycle fake");
    }

    private sealed class FakeCamera(CameraKind kind) : IVirtualCamera
    {
        public float DefaultFoV { get; private set; }
        public float DefaultRoll { get; private set; }
        public System.Numerics.Vector3 DefaultRotation { get; private set; }
        public void CaptureOwnedDefaults()
        {
            DefaultFoV = FoV;
            DefaultRoll = Roll;
            DefaultRotation = Rotation;
        }

        public bool IsValid { get; set; } = true;
        public string Name { get; set; } = "Camera";
        public CameraKind Kind { get; } = kind;
        public bool IsLive { get; set; }
        public bool IsDefault { get; set; }
        public bool IsLocked { get; set; }
        public Vector2 Angle { get; set; }
        public Vector2 Pan { get; set; }
        public float Roll { get; set; }
        public float Zoom { get; set; }
        public Vector2 ZoomLimits => Vector2.Zero;
        public float FoV { get; set; }
        public Vector3 PositionOffset { get; set; }
        public Vector3? FixedPosition { get; set; }
        public Vector3 TargetOffset { get; set; }
        public string TargetActorName { get; set; } = string.Empty;
        public IActor? TargetActor { get; set; }
        public ActorId? TargetActorId { get; set; }
        public bool IsTargetLocked { get; set; }
        public Vector3 WorldPosition => Vector3.Zero;
        public bool DisableCollision { get; set; }
        public bool DelimitCamera { get; set; }
        public bool IsPortraitMode => false;
        public void TogglePortraitMode() { }
        public Vector3 Position { get; set; }
        public Vector3 SpawnPosition => Vector3.Zero;
        public Vector3 Rotation { get; set; }
        public bool MovementEnabled { get; set; }
        public bool Move2D { get; set; }
        public float MovementSpeed { get; set; }
        public float MouseSensitivity { get; set; }
        public bool DelimitAngle { get; set; }
        public bool Orthographic { get; set; }
        public float OrthographicZoom { get; set; }
        public bool IsTracking { get; set; }
        public CameraTrackingMode TrackingMode { get; set; }
        public IList<IBone> TrackedBones { get; } = new List<IBone>();
        public void ResetProperties() { }
    }

    /// <summary>The prop half at its port: a token per spawned prop, with the
    /// state an entry reads and writes back.</summary>
    /// <summary>
    /// The adopted-world-object half at its port: a MAP the claims are taken
    /// against, so a release genuinely gives the object back and a re-adoption
    /// finds it again — the one property that separates this half from every
    /// other, all of which destroy and re-create.
    /// </summary>
    private sealed class FakeWorldObjects : IWorldObjectLifecycle
    {
        private readonly Dictionary<nint, Transform> _map = new();
        private readonly Dictionary<nint, WorldObjectIncarnation> _identities = new();
        private readonly HashSet<nint> _vfxAddresses = new();
        private readonly List<object> _adopted = new();
        private long _nextGeneration;
        public int AdoptCalls { get; private set; }
        public int SpawnCalls { get; private set; }

        public bool RefuseAdopt { get; set; }
        public bool RefuseRelease { get; set; }
        public HashSet<nint> RefuseReleaseAddresses { get; } = new();
        public HashSet<string> RefuseReleasePaths { get; } = new();
        public IReadOnlyList<object> Live => _adopted;
        public IReadOnlyList<object> WorldObjects => _adopted.ToList();

        /// <summary>Where the map stands one address, which is what every
        /// release has to put back.</summary>
        public Transform MapPlacement(nint address) => _map[address];

        public nint Place(nint address, Transform placement, bool isVfx = false)
        {
            _map[address] = placement;
            if (isVfx) _vfxAddresses.Add(address);
            else _vfxAddresses.Remove(address);
            long generation = ++_nextGeneration;
            _identities[address] = new WorldObjectIncarnation(
                address, generation, isVfx ? (nint)generation : nint.Zero, isVfx);
            return address;
        }

        public void Remove(nint address)
        {
            _map.Remove(address);
            _identities.Remove(address);
            _vfxAddresses.Remove(address);
        }

        public TransformTargetId Target(object worldObject) =>
            TransformTargetId.ForWorldObject(((FakeWorldObject)worldObject).Id);

        public object? Adopt(nint address)
        {
            AdoptCalls++;
            if (RefuseAdopt || !_map.ContainsKey(address))
                return null;
            var claim = new FakeWorldObject
            {
                Owner = this,
                State = new WorldObjectState(
                    address, "bg/fake.mdl", false, _map[address], true)
                {
                    Identity = _identities[address],
                },
                IsVfx = _vfxAddresses.Contains(address),
                MapPlacement = _map[address],
            };
            _adopted.Add(claim);
            return claim;
        }

        public bool CanReclaim(WorldObjectState state, out string detail)
        {
            if (!_map.ContainsKey(state.Address))
            {
                detail = "Unable to undo: the original world object is no longer in the current world graph.";
                return false;
            }
            var current = _identities[state.Address];
            bool matches = state.Identity.IsVfx
                ? current == state.Identity
                : current.SameAllocation(state.Identity);
            detail = matches
                ? string.Empty
                : "Unable to undo: the world object at this address no longer matches the captured identity.";
            return matches;
        }

        public object? Reclaim(WorldObjectState state, out string detail)
        {
            if (!CanReclaim(state, out detail))
                return null;
            return Adopt(state.Address);
        }

        public object? Spawn(string path, Transform placement, bool visible)
        {
            SpawnCalls++;
            var claim = new FakeWorldObject
            {
                Owner = this,
                State = new WorldObjectState(
                    nint.Zero, path, true, placement, visible),
                MapPlacement = placement,
            };
            _adopted.Add(claim);
            return claim;
        }

        public bool IsLive(object worldObject) =>
            ((FakeWorldObject)worldObject).IsValid;

        public bool Release(object worldObject)
        {
            var state = ((FakeWorldObject)worldObject).State;
            if (RefuseRelease || RefuseReleaseAddresses.Contains(state.Address)
                || RefuseReleasePaths.Contains(state.Path))
                return false;
            var claim = (FakeWorldObject)worldObject;
            _adopted.Remove(claim);
            if (claim.IsValid)
                _map[claim.State.Address] = claim.MapPlacement;
            claim.IsValid = false;
            return true;
        }

        public WorldObjectState Read(object worldObject) =>
            ((FakeWorldObject)worldObject).State;

        public void Apply(object worldObject, WorldObjectState state)
        {
            var claim = (FakeWorldObject)worldObject;
            claim.State = state;
            _map[state.Address] = state.Placement;
        }
    }

    private sealed class FakeWorldObject
    {
        public FakeWorldObjects Owner { get; set; } = null!;
        public WorldObjectId Id { get; } = WorldObjectId.New();
        public bool IsVfx { get; set; }
        public bool IsValid { get; set; } = true;
        public WorldObjectState State { get; set; }

        /// <summary>The map's own placement, captured at adoption and written
        /// back on release. It is the fake's stand-in for the service's
        /// InitialPlacement.</summary>
        public Transform MapPlacement { get; set; }
    }

    private sealed class FakeProps : IPropLifecycle
    {
        private readonly List<object> _props = new();

        public bool RefuseSpawn { get; set; }
        public IReadOnlyList<object> Live => _props;
        public IReadOnlyList<object> Props => _props.ToList();

        public object? Spawn(PropModel model)
        {
            if (RefuseSpawn)
                return null;
            var prop = new FakeProp
            {
                State = new PropState(model.Name, model, Transform.Identity, true),
            };
            _props.Add(prop);
            return prop;
        }

        public bool IsLive(object prop) => ((FakeProp)prop).IsValid;

        public void Destroy(object prop)
        {
            _props.Remove(prop);
            ((FakeProp)prop).IsValid = false;
        }

        /// <summary>The prop leaves without this seam's knowledge — a scene
        /// import, or the game.</summary>
        public void VanishWithoutNotice(object prop) => Destroy(prop);

        public PropState Read(object prop) => ((FakeProp)prop).State;

        public void Apply(object prop, PropState state) =>
            ((FakeProp)prop).State = ((FakeProp)prop).State with
            {
                Name = state.Name,
                Transform = state.Transform,
                Visible = state.Visible,
            };
    }

    private sealed class FakeProp : IPropHandle
    {
        public PropId StableId { get; } = new(Guid.NewGuid(), 1);
        public bool IsValid { get; set; } = true;
        public PropState State { get; set; }
        public int Id => 0;
        public nint Address => 0;
        public string Name { get => State.Name; set => State = State with { Name = value }; }
        public PropModel Model => State.Model;
        public bool Visible { get => State.Visible; set => State = State with { Visible = value }; }
        public Transform Transform { get => State.Transform; set => State = State with { Transform = value }; }
        public Vector3 Position { get => Transform.Position; set { var t = Transform; t.Position = value; Transform = t; } }
        public Quaternion Rotation { get => Transform.Rotation; set { var t = Transform; t.Rotation = value; Transform = t; } }
        public Vector3 Scale { get => Transform.Scale; set { var t = Transform; t.Scale = value; Transform = t; } }
        public bool Respawn(PropModel model, out string? detail)
        {
            State = State with { Model = model };
            detail = null;
            return IsValid;
        }
        public void Destroy() => IsValid = false;
    }

    private sealed class FakeOverlays : IOverlayLifecycle
    {
        private readonly List<object> _overlays = new();

        public IReadOnlyList<object> Live => _overlays;
        public IReadOnlyList<object> Overlays => _overlays.ToList();

        public object? Create(OverlayNodeState state)
        {
            var overlay = new FakeOverlay { State = state };
            _overlays.Add(overlay);
            return overlay;
        }

        public bool IsLive(object overlay) => ((FakeOverlay)overlay).IsValid;

        public void Destroy(object overlay)
        {
            _overlays.Remove(overlay);
            ((FakeOverlay)overlay).IsValid = false;
        }

        public void VanishWithoutNotice(object overlay) => Destroy(overlay);

        public OverlayNodeState Read(object overlay) =>
            ((FakeOverlay)overlay).State;

        public void Write(object overlay, Func<OverlayNodeState, OverlayNodeState> edit) =>
            ((FakeOverlay)overlay).State = edit(((FakeOverlay)overlay).State);

    }

    private sealed class FakeOverlay : IOverlayNode
    {
        public OverlayId StableId { get; } = new(Guid.NewGuid(), 1);
        public bool IsValid { get; set; } = true;
        public OverlayNodeState State { get; set; } = new();
        public int Id => 0;
        public OverlayNodeKind Kind => State.Kind;
        public string Name { get => State.Name; set => State = State with { Name = value }; }
        public Vector2 Position { get => State.Position; set => State = State with { Position = value }; }
        public float Scale { get => State.Scale; set => State = State with { Scale = value }; }
        public float Alpha { get => State.Alpha; set => State = State with { Alpha = value }; }
        public bool Visible { get => State.Visible; set => State = State with { Visible = value }; }
        public bool Draggable { get => State.Draggable; set => State = State with { Draggable = value }; }
        public string Text { get => State.Text; set => State = State with { Text = value }; }
        public string Speaker { get => State.Speaker; set => State = State with { Speaker = value }; }
        public uint FontSize { get => State.FontSize; set => State = State with { FontSize = value }; }
        public TalkBackground TalkBackground { get => State.TalkBackground; set => State = State with { TalkBackground = value }; }
        public TalkCursor TalkCursor { get => State.TalkCursor; set => State = State with { TalkCursor = value }; }
        public BalloonChannel BalloonChannel { get => State.BalloonChannel; set => State = State with { BalloonChannel = value }; }
        public BalloonGradient BalloonGradient { get => State.BalloonGradient; set => State = State with { BalloonGradient = value }; }
        public bool ArrowVisible { get => State.ArrowVisible; set => State = State with { ArrowVisible = value }; }
        public float ArrowX { get => State.ArrowX; set => State = State with { ArrowX = value }; }
        public StatusKind StatusKind { get => State.StatusKind; set => State = State with { StatusKind = value }; }
        public uint StatusIconId { get => State.StatusIconId; set => State = State with { StatusIconId = value }; }
        public void Destroy() => IsValid = false;
    }

    [Fact]
    public void Plain_copy_inherits_body_profile_but_posed_copy_does_not_apply_it_twice()
    {
        var world = new World();
        var source = world.Actors.Spawn("Source")!;
        var plain = world.Lifecycle.SpawnActor("Copy", () => world.Actors.Spawn("Copy"), source)!;
        var posed = world.Lifecycle.SpawnActorWithPose("Copy posed", () => world.Actors.Spawn("Posed"), source);
        Assert.Same(posed, Assert.Single(world.Actors.DetachedGaze));
        Assert.Equal((source, plain), Assert.Single(world.Actors.BodyProfileCopies));
    }

    private sealed class FakeActors : IActorLifecycle
    {
        public List<object> DetachedGaze { get; } = new();
        public void DetachGaze(object actor) => DetachedGaze.Add(actor);
        public List<(IActor Source, IActor Target)> BodyProfileCopies { get; } = new();
        public void CopyBodyProfile(IActor source, IActor target) => BodyProfileCopies.Add((source, target));
        public string GetName(object actor) => ((IActor)actor).Name;
        public void SetName(object actor, string name) => ((IActor)actor).Name = name;
        public void NameCreated(object actor, string seed) => SetName(actor,
            EntityNames.Next(seed, _actors.Where(x => !ReferenceEquals(x, actor)).Select(x => x.Name)));

        private readonly List<IActor> _actors = new();

        /// <summary>What each live actor currently IS, so a removal's capture
        /// and a restore's re-application are observable without a body.
        /// </summary>
        private readonly Dictionary<IActor, ActorState> _states =
            new(ReferenceEqualityComparer.Instance);

        private int _next;

        public IReadOnlyList<IActor> Live => _actors;
        public int SpawnCalls { get; private set; }
        public bool RefuseDestroy { get; set; }

        /// <summary>Every refusal the seam named rather than skipping.
        /// </summary>
        public List<string> Notes { get; } = new();

        public IActor? Spawn(string name)
        {
            SpawnCalls++;
            var actor = new ActorBase(
                new EntityId($"{name}-{_next++}"),
                name,
                (nint)(_next + 1),
                ActorKind.Player);
            _actors.Add(actor);
            _states[actor] = new ActorState(Transform.Identity, true, null);
            return actor;
        }

        /// <summary>What the user made of a live actor after it was spawned.
        /// </summary>
        public void Edit(IActor actor, ActorState state) => _states[actor] = state;

        public ActorState StateOf(IActor actor) => _states[actor];

        /// <summary>The actor leaves without this seam's knowledge — a scene
        /// import, or the game.</summary>
        public void DestroyActor(IActor actor) => Destroy(actor);

        public bool IsSpawned(object actor) => _actors.Contains((IActor)actor);

        public bool Destroy(object actor)
        {
            if (RefuseDestroy)
                return false;
            _states.Remove((IActor)actor);
            return _actors.Remove((IActor)actor);
        }

        public void WhenPosable(object actor, Action<object> act) => act(actor);

        public ActorState Read(object actor) => _states[(IActor)actor];

        public IActor? Recreate(ActorState state) => Spawn("Fresh body");

        public void Restore(object actor, ActorState state, Func<bool>? stillCurrent = null)
        {
            if (stillCurrent?.Invoke() != false)
                _states[(IActor)actor] = state;
        }

        public void Note(string detail) => Notes.Add(detail);
    }
}
