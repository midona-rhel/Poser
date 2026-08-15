using System.Collections;
using System.Numerics;
using System.Reflection;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using Poser.Entities;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The lifecycle seam's contract: an add or a remove is one entry in the
/// SAME history the transforms use, its two directions are exact inverses,
/// and the entity's IDENTITY survives a destroy/respawn pair so entries
/// stacked on one entity keep naming that entity rather than its corpse.
/// </summary>
public sealed class SceneLifecycleHistoryTests
{
    // ── lights ───────────────────────────────────────────────────────────

    [Fact]
    public void Adding_a_light_leaves_one_undoable_entry_that_destroys_it()
    {
        var world = new World();

        var light = world.Lifecycle.SpawnLight(LightKind.Spot);

        Assert.NotNull(light);
        Assert.Equal("Add spot light", world.History.UndoDescription);
        Assert.Single(world.Lighting.Live);

        Assert.True(world.Undo());
        Assert.Empty(world.Lighting.Live);
        Assert.False(world.History.CanUndo);
        Assert.True(world.History.CanRedo);
    }

    [Fact]
    public void Redoing_an_add_restores_the_light_as_the_user_last_had_it()
    {
        var world = new World();
        var light = world.Lifecycle.SpawnLight(LightKind.Point)!;
        light.Name = "Key";
        light.Intensity = 7.5f;
        light.Kind = LightKind.Area;

        Assert.True(world.Undo());
        Assert.True(world.Redo());

        var restored = Assert.Single(world.Lighting.Live);
        Assert.NotSame(light, restored);
        Assert.Equal("Key", restored.Name);
        Assert.Equal(7.5f, restored.Intensity);
        Assert.Equal(LightKind.Area, restored.Kind);
    }

    [Fact]
    public void Removing_a_light_is_undone_by_bringing_the_same_light_back()
    {
        var world = new World();
        var light = world.Lighting.SpawnLight(LightKind.Spot)!;
        light.Name = "Rim";

        world.Lifecycle.DestroyLight(light);

        Assert.Empty(world.Lighting.Live);
        Assert.Equal("Remove light 'Rim'", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal("Rim", Assert.Single(world.Lighting.Live).Name);
    }

    /// <summary>
    /// The regression the slot registry exists for: an add and a later remove
    /// are two entries about ONE light. Undoing past the remove must destroy
    /// the light the remove's own undo just re-created — not the original,
    /// which by then is a corpse, leaving the replacement standing.
    /// </summary>
    [Fact]
    public void Undo_past_a_removal_destroys_the_light_the_removal_restored()
    {
        var world = new World();
        var original = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        world.Lifecycle.DestroyLight(original);

        Assert.True(world.Undo());   // the removal: the light comes back
        var restored = Assert.Single(world.Lighting.Live);
        Assert.NotSame(original, restored);

        Assert.True(world.Undo());   // the add: nothing may be left standing
        Assert.Empty(world.Lighting.Live);
    }

    [Fact]
    public void A_light_the_game_refuses_to_respawn_keeps_its_entry()
    {
        var world = new World();
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        Assert.True(world.Undo());

        world.Lighting.RefuseSpawn = true;
        Assert.False(world.Redo());

        // The step is still there to be redone: a refused act consumes no
        // history.
        Assert.True(world.History.CanRedo);
        Assert.Empty(world.Lighting.Live);
        world.Lighting.RefuseSpawn = false;
        Assert.True(world.Redo());
        Assert.Single(world.Lighting.Live);
    }

    [Fact]
    public void A_borrowed_light_is_released_without_taking_an_entry()
    {
        var world = new World();
        var borrowed = world.Lighting.AddBorrowed();

        world.Lifecycle.DestroyLight(borrowed);

        Assert.Empty(world.Lighting.Live);
        Assert.False(world.History.CanUndo);
    }

    [Fact]
    public void A_light_that_left_by_another_path_undoes_without_a_dead_write()
    {
        var world = new World();
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;

        // A scene import, or the game itself, took it: nothing this seam did.
        world.Lighting.VanishWithoutNotice(light);

        // Undo has nothing left to remove and says so honestly, rather than
        // failing on a corpse and pinning every older entry behind it.
        Assert.True(world.Undo());
        Assert.Empty(world.Lighting.Live);
        // Nothing was ever documented, so the redo refuses instead of minting
        // a default-valued impostor wearing the entry's name.
        Assert.False(world.Redo());
    }

    // ── cameras ──────────────────────────────────────────────────────────

    [Fact]
    public void Adding_a_camera_is_undoable_and_redo_restores_its_framing()
    {
        var world = new World();
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        camera.Name = "Wide";
        camera.Zoom = 12f;

        Assert.Equal("Add free camera", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Empty(world.Cameras.Live);

        Assert.True(world.Redo());
        var restored = Assert.Single(world.Cameras.Live);
        Assert.Equal("Wide", restored.Name);
        Assert.Equal(12f, restored.Zoom);
        Assert.Equal(CameraKind.Free, restored.Kind);
    }

    [Fact]
    public void Undo_past_a_camera_removal_destroys_the_restored_camera()
    {
        var world = new World();
        var original = world.Lifecycle.CreateCamera(CameraKind.Game)!;
        world.Lifecycle.DestroyCamera(original);

        Assert.True(world.Undo());
        Assert.NotSame(original, Assert.Single(world.Cameras.Live));
        Assert.True(world.Undo());
        Assert.Empty(world.Cameras.Live);
    }

    [Fact]
    public void The_gpose_camera_is_destroyed_without_taking_an_entry()
    {
        var world = new World();
        var session = world.Cameras.AddDefault();

        world.Lifecycle.DestroyCamera(session);

        Assert.Empty(world.Cameras.Live);
        Assert.False(world.History.CanUndo);
    }

    // ── actors ───────────────────────────────────────────────────────────

    [Fact]
    public void An_actor_spawn_is_undone_by_a_despawn_and_redone_by_respawning()
    {
        var world = new World();

        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("Actor"));

        Assert.NotNull(actor);
        Assert.Equal("Add actor", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Empty(world.Actors.Live);

        Assert.True(world.Redo());
        Assert.NotSame(actor, Assert.Single(world.Actors.Live));
        Assert.Equal(2, world.Actors.SpawnCalls);
    }

    [Fact]
    public void An_actor_despawned_elsewhere_still_lets_its_add_undo()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("Actor"))!;

        // The actor context menu's own Despawn: not routed through this seam.
        world.Actors.DestroyActor(actor);

        Assert.True(world.Undo());
        Assert.Empty(world.Actors.Live);
        Assert.True(world.Redo());
        Assert.Single(world.Actors.Live);
    }

    [Fact]
    public void A_spawn_the_game_refuses_records_nothing()
    {
        var world = new World();

        Assert.Null(world.Lifecycle.SpawnActor("Add actor", () => null));

        Assert.False(world.History.CanUndo);
    }

    /// <summary>An actor edited after its spawn comes back edited, exactly as
    /// a light and a prop do: the document is captured when the actor LEAVES,
    /// not when it was born.</summary>
    [Fact]
    public void Redoing_a_spawn_brings_the_actor_back_as_the_user_had_it()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("Actor"))!;
        world.Actors.Edit(actor, Posed(new Vector3(2f, 0f, 5f), visible: false));

        Assert.True(world.Undo());
        Assert.True(world.Redo());

        var restored = Assert.Single(world.Actors.Live);
        var state = world.Actors.StateOf(restored);
        Assert.Equal(new Vector3(2f, 0f, 5f), state.Placement.Position);
        Assert.False(state.Visible);
        Assert.NotNull(state.Pose);
    }

    [Fact]
    public void Despawning_an_actor_this_seam_spawned_brings_it_back_as_it_stood()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("Lead"))!;
        world.Actors.Edit(actor, Posed(new Vector3(-1f, 0f, 3f), visible: true));

        world.Lifecycle.DespawnActor(actor);

        Assert.Empty(world.Actors.Live);
        Assert.Equal("Despawn actor 'Lead'", world.History.UndoDescription);
        Assert.Empty(world.Actors.Notes);

        Assert.True(world.Undo());

        var restored = Assert.Single(world.Actors.Live);
        Assert.NotSame(actor, restored);
        var state = world.Actors.StateOf(restored);
        Assert.Equal(new Vector3(-1f, 0f, 3f), state.Placement.Position);
        Assert.NotNull(state.Pose);
    }

    /// <summary>The slot-identity regression, on the actor half: the despawn's
    /// own undo minted a NEW actor, and undoing past it has to take THAT one
    /// away rather than the corpse the spawn entry was born holding.</summary>
    [Fact]
    public void Undo_past_a_despawn_destroys_the_actor_the_despawn_restored()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("Lead"))!;
        world.Lifecycle.DespawnActor(actor);

        Assert.True(world.Undo());
        Assert.NotSame(actor, Assert.Single(world.Actors.Live));

        Assert.True(world.Undo());
        Assert.Empty(world.Actors.Live);
    }

    /// <summary>
    /// The despawns that stay unundoable, and the point of the test: they are
    /// NAMED, not silently skipped. Poser never recorded spawning this actor
    /// — the world tab's clone and the overlay's adoption both reach the spawn
    /// service directly — so there is no call to run again.
    /// </summary>
    [Fact]
    public void Despawning_an_actor_nobody_recorded_spawning_says_why_it_cannot_be_undone()
    {
        var world = new World();
        var actor = world.Actors.Spawn("Stranger")!;

        world.Lifecycle.DespawnActor(actor);

        Assert.Empty(world.Actors.Live);
        Assert.False(world.History.CanUndo);
        var note = Assert.Single(world.Actors.Notes);
        Assert.Contains("Stranger", note);
        Assert.Contains("cannot be undone", note);
    }

    [Fact]
    public void A_despawn_the_game_refuses_records_no_entry()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("Lead"))!;
        world.Actors.RefuseDestroy = true;

        world.Lifecycle.DespawnActor(actor);

        // The actor is still there, so nothing was stacked on top of the add
        // that put it there.
        Assert.Single(world.Actors.Live);
        Assert.Equal("Add actor", world.History.UndoDescription);
    }

    private static ActorState Posed(Vector3 position, bool visible)
    {
        var placement = Transform.Identity;
        placement.Position = position;
        return new ActorState(placement, visible, new Poser.Files.PoseFile());
    }

    // ── props ────────────────────────────────────────────────────────────

    private static readonly PropModel Apple =
        new("Apple", 9001, 249, 1, "The default prop");

    [Fact]
    public void Adding_a_prop_leaves_one_undoable_entry_that_destroys_it()
    {
        var world = new World();

        var prop = world.Lifecycle.SpawnProp(Apple);

        Assert.NotNull(prop);
        Assert.Equal("Add object 'Apple'", world.History.UndoDescription);
        Assert.Single(world.Props.Live);

        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
        Assert.False(world.History.CanUndo);
        Assert.True(world.History.CanRedo);
    }

    [Fact]
    public void Redoing_an_add_restores_the_prop_where_the_user_left_it()
    {
        var world = new World();
        var prop = world.Lifecycle.SpawnProp(Apple)!;
        var moved = Transform.Identity;
        moved.Position = new Vector3(3f, 1f, -2f);
        world.Props.Apply(prop, new PropState("Apple", Apple, moved, false));

        Assert.True(world.Undo());
        Assert.True(world.Redo());

        var restored = Assert.Single(world.Props.Live);
        Assert.NotSame(prop, restored);
        var state = world.Props.Read(restored);
        Assert.Equal(moved.Position, state.Transform.Position);
        Assert.False(state.Visible);
        Assert.Equal(Apple.Model, state.Model.Model);
    }

    [Fact]
    public void Removing_a_prop_is_undone_by_bringing_the_same_prop_back()
    {
        var world = new World();
        var prop = world.Lifecycle.SpawnProp(Apple)!;

        world.Lifecycle.DestroyProp(prop);

        Assert.Empty(world.Props.Live);
        Assert.Equal("Remove object 'Apple'", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Single(world.Props.Live);
    }

    [Fact]
    public void Undo_past_a_prop_removal_destroys_the_prop_the_removal_restored()
    {
        var world = new World();
        var original = world.Lifecycle.SpawnProp(Apple)!;
        world.Lifecycle.DestroyProp(original);

        Assert.True(world.Undo());
        Assert.NotSame(original, Assert.Single(world.Props.Live));
        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
    }

    [Fact]
    public void A_prop_the_game_refuses_to_respawn_keeps_its_entry()
    {
        var world = new World();
        world.Lifecycle.SpawnProp(Apple);
        Assert.True(world.Undo());

        world.Props.RefuseSpawn = true;
        Assert.False(world.Redo());

        Assert.True(world.History.CanRedo);
        Assert.Empty(world.Props.Live);
        world.Props.RefuseSpawn = false;
        Assert.True(world.Redo());
        Assert.Single(world.Props.Live);
    }

    [Fact]
    public void A_prop_that_left_by_another_path_undoes_without_a_dead_write()
    {
        var world = new World();
        var prop = world.Lifecycle.SpawnProp(Apple)!;

        world.Props.VanishWithoutNotice(prop);

        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
        Assert.False(world.Redo());
    }

    /// <summary>Clearing the list is ONE act, so it is ONE step of the user's
    /// history however many props it took.</summary>
    [Fact]
    public void Removing_every_prop_is_one_entry_that_brings_them_all_back()
    {
        var world = new World();
        world.Lifecycle.SpawnProp(Apple);
        world.Lifecycle.SpawnProp(Apple with { Name = "Lamp" });

        world.Lifecycle.DestroyAllProps();

        Assert.Empty(world.Props.Live);
        Assert.Equal("Remove 2 objects", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal(2, world.Props.Live.Count);
        // And the two adds are still there behind it.
        Assert.Equal("Add object 'Lamp'", world.History.UndoDescription);
    }

    [Fact]
    public void Cloning_a_prop_copies_where_it_stands_but_not_what_it_is_called()
    {
        var world = new World();
        var source = world.Lifecycle.SpawnProp(Apple)!;
        var moved = Transform.Identity;
        moved.Position = new Vector3(4f, 0f, 1f);
        world.Props.Apply(source, new PropState("Fruit", Apple, moved, false));

        var clone = world.Lifecycle.CloneProp(source);

        Assert.NotNull(clone);
        Assert.Equal(2, world.Props.Live.Count);
        var state = world.Props.Read(clone!);
        Assert.Equal(moved.Position, state.Transform.Position);
        Assert.False(state.Visible);
        Assert.Equal(Apple.Model, state.Model.Model);
        // A spawn names itself; only the undo of a REMOVAL puts a user's own
        // name back.
        Assert.Equal("Apple", state.Name);
        Assert.Equal("Clone object 'Fruit'", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Single(world.Props.Live);
    }

    [Fact]
    public void A_spawn_the_game_refuses_records_no_prop_entry()
    {
        var world = new World { Props = { RefuseSpawn = true } };

        Assert.Null(world.Lifecycle.SpawnProp(Apple));

        Assert.False(world.History.CanUndo);
    }

    // ── overlay nodes ────────────────────────────────────────────────────

    [Fact]
    public void Adding_an_overlay_leaves_one_undoable_entry_that_removes_it()
    {
        var world = new World();

        var overlay = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk);

        Assert.NotNull(overlay);
        Assert.Equal("Add dialog 'Dialog'", world.History.UndoDescription);
        Assert.Single(world.Overlays.Live);

        Assert.True(world.Undo());
        Assert.Empty(world.Overlays.Live);
        Assert.True(world.History.CanRedo);
    }

    [Fact]
    public void An_overlay_entry_names_the_kind_it_added()
    {
        var world = new World();

        world.Lifecycle.SpawnOverlay(OverlayNodeKind.Balloon);
        Assert.Equal("Add balloon 'Balloon'", world.History.UndoDescription);

        world.Lifecycle.SpawnOverlay(OverlayNodeKind.Status);
        Assert.Equal("Add status 'Status'", world.History.UndoDescription);
    }

    /// <summary>The whole point of capturing at the moment of REMOVAL: a node
    /// the user rewrote comes back saying what they last made it say, not what
    /// it was born saying.</summary>
    [Fact]
    public void Redoing_an_add_restores_the_overlay_as_the_user_last_had_it()
    {
        var world = new World();
        var overlay = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk)!;
        world.Overlays.Write(overlay, state => state with
        {
            Speaker = "Y'shtola",
            Text = "The aether stirs.",
            TalkBackground = TalkBackground.Linkpearl,
        });

        Assert.True(world.Undo());
        Assert.True(world.Redo());

        var restored = Assert.Single(world.Overlays.Live);
        Assert.NotSame(overlay, restored);
        var document = world.Overlays.Read(restored);
        Assert.Equal("Y'shtola", document.Speaker);
        Assert.Equal("The aether stirs.", document.Text);
        Assert.Equal(TalkBackground.Linkpearl, document.TalkBackground);
    }

    [Fact]
    public void Removing_an_overlay_is_undone_by_bringing_the_same_one_back()
    {
        var world = new World();
        var overlay = world.Overlays.Create(
            OverlayNodeService.DefaultState(OverlayNodeKind.Status) with
            {
                Name = "Astral Fire",
            })!;

        world.Lifecycle.DestroyOverlay(overlay);

        Assert.Empty(world.Overlays.Live);
        Assert.Equal(
            "Remove status 'Astral Fire'", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal(
            "Astral Fire",
            world.Overlays.Read(Assert.Single(world.Overlays.Live)).Name);
    }

    /// <summary>The slot-registry regression, for overlays: undoing past a
    /// removal must destroy the node the removal's own undo re-created, not
    /// the corpse the add was born holding.</summary>
    [Fact]
    public void Undo_past_a_removal_destroys_the_overlay_the_removal_restored()
    {
        var world = new World();
        var original = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk)!;
        world.Lifecycle.DestroyOverlay(original);

        Assert.True(world.Undo());
        Assert.NotSame(original, Assert.Single(world.Overlays.Live));
        Assert.True(world.Undo());
        Assert.Empty(world.Overlays.Live);
    }

    [Fact]
    public void An_overlay_the_game_refuses_to_recreate_keeps_its_entry()
    {
        var world = new World();
        world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk);
        Assert.True(world.Undo());

        world.Overlays.RefuseCreate = true;
        Assert.False(world.Redo());

        Assert.True(world.History.CanRedo);
        Assert.Empty(world.Overlays.Live);
        world.Overlays.RefuseCreate = false;
        Assert.True(world.Redo());
        Assert.Single(world.Overlays.Live);
    }

    [Fact]
    public void An_overlay_that_left_by_another_path_undoes_without_a_dead_write()
    {
        var world = new World();
        var overlay = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk)!;

        world.Overlays.VanishWithoutNotice(overlay);

        Assert.True(world.Undo());
        Assert.Empty(world.Overlays.Live);
        Assert.False(world.Redo());
    }

    [Fact]
    public void Removing_every_overlay_is_one_entry_that_brings_them_all_back()
    {
        var world = new World();
        world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk);
        world.Lifecycle.SpawnOverlay(OverlayNodeKind.Balloon);

        world.Lifecycle.DestroyAllOverlays();

        Assert.Empty(world.Overlays.Live);
        Assert.Equal("Remove 2 overlays", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal(2, world.Overlays.Live.Count);
        Assert.Equal("Add balloon 'Balloon'", world.History.UndoDescription);
    }

    [Fact]
    public void A_create_the_game_refuses_records_no_overlay_entry()
    {
        var world = new World { Overlays = { RefuseCreate = true } };

        Assert.Null(world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk));

        Assert.False(world.History.CanUndo);
    }

    // ── group removal ────────────────────────────────────────────────────

    [Fact]
    public void Removing_a_selection_of_props_is_one_entry_over_all_of_them()
    {
        var world = new World();
        var first = world.Lifecycle.SpawnProp(Apple)!;
        var second = world.Lifecycle.SpawnProp(Apple with { Name = "Lamp" })!;

        var removed = world.Lifecycle.DestroySelection(
            props: new[] { first, second });

        Assert.Equal(2, removed);
        Assert.Empty(world.Props.Live);
        Assert.Equal("Remove 2 entities", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal(2, world.Props.Live.Count);
        // And the two adds are still there behind the one group entry.
        Assert.Equal("Add object 'Lamp'", world.History.UndoDescription);
    }

    [Fact]
    public void One_entry_covers_a_selection_of_several_kinds_at_once()
    {
        var world = new World();
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        var prop = world.Lifecycle.SpawnProp(Apple)!;
        var overlay = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk)!;
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;

        var removed = world.Lifecycle.DestroySelection(
            props: new[] { prop },
            lights: new[] { light },
            cameras: new[] { camera },
            overlays: new[] { overlay });

        Assert.Equal(4, removed);
        Assert.Equal("Remove 4 entities", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Single(world.Lighting.Live);
        Assert.Single(world.Props.Live);
        Assert.Single(world.Overlays.Live);
        Assert.Single(world.Cameras.Live);
    }

    /// <summary>
    /// A despawn has no inverse this seam can state, so selected actors are
    /// destroyed OUTSIDE the entry — they are counted as removed and the
    /// entry that remains is about the rest.
    /// </summary>
    [Fact]
    public void Actors_in_a_selection_are_destroyed_without_joining_the_entry()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("A"))!;
        var prop = world.Lifecycle.SpawnProp(Apple)!;

        var removed = world.Lifecycle.DestroySelection(
            new[] { actor }, new[] { prop });

        Assert.Equal(2, removed);
        Assert.Empty(world.Actors.Live);
        Assert.Equal("Remove 1 entity", world.History.UndoDescription);
        Assert.True(world.Undo());
        // The prop comes back; the actor does not, and never claimed it would.
        Assert.Single(world.Props.Live);
        Assert.Empty(world.Actors.Live);
    }

    /// <summary>The group op inherits the single-actor rule: an actor this
    /// seam spawned despawns as a journaled step, exactly as if its own
    /// context menu had been used.</summary>
    [Fact]
    public void A_selection_of_actors_despawns_through_the_journal()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor(
            "Add actor", () => world.Actors.Spawn("A"))!;

        var removed = world.Lifecycle.DestroySelection(new[] { actor });

        Assert.Equal(1, removed);
        Assert.Empty(world.Actors.Live);
        Assert.Equal("Despawn actor 'A'", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Single(world.Actors.Live);
    }

    /// <summary>A borrowed light is released, not destroyed, and a release has
    /// no spawn that inverts it — the single-light rule, applied per member of
    /// a selection.</summary>
    [Fact]
    public void A_borrowed_light_in_a_selection_is_released_without_an_entry()
    {
        var world = new World();
        var borrowed = world.Lighting.AddBorrowed();

        var removed = world.Lifecycle.DestroySelection(
            lights: new[] { borrowed });

        Assert.Equal(1, removed);
        Assert.Empty(world.Lighting.Live);
        Assert.False(world.History.CanUndo);
    }

    /// <summary>The session's own camera cannot be destroyed at all, so a
    /// selection holding it removes everything else and leaves it standing.
    /// </summary>
    [Fact]
    public void The_session_camera_in_a_selection_is_left_where_it_is()
    {
        var world = new World();
        var session = world.Cameras.AddDefault();

        var removed = world.Lifecycle.DestroySelection(
            cameras: new[] { session });

        Assert.Equal(0, removed);
        Assert.Single(world.Cameras.Live);
        Assert.False(world.History.CanUndo);
    }

    [Fact]
    public void An_empty_selection_removes_nothing_and_records_nothing()
    {
        var world = new World();

        Assert.Equal(0, world.Lifecycle.DestroySelection());

        Assert.False(world.History.CanUndo);
    }

    // ── adopted world objects ────────────────────────────────────────────

    /// <summary>The map's own placement, and where the user drags the object.
    /// </summary>
    private static readonly Transform MapStood = new(
        new Vector3(4f, 0f, 8f), Quaternion.Identity, Vector3.One);

    private static readonly Transform UserPut = new(
        new Vector3(40f, 6f, 80f), Quaternion.Identity, new Vector3(2f, 2f, 2f));

    [Fact]
    public void Adopting_a_world_object_takes_an_entry()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);

        Assert.NotNull(world.Lifecycle.AdoptWorldObject(address));

        Assert.Equal("Add world object", world.History.UndoDescription);
    }

    [Fact]
    public void Undoing_an_adoption_releases_it_and_gives_the_map_it_back()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(
            claim, new WorldObjectState(address, UserPut, true));

        Assert.True(world.Undo());

        Assert.Empty(world.WorldObjects.Live);
        // The map has its object back where the map stood it — an adoption's
        // undo is a RESTORE, never a destroy.
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));
    }

    [Fact]
    public void Redoing_an_adoption_takes_the_same_address_and_the_users_placement()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(
            claim, new WorldObjectState(address, UserPut, false));
        world.Undo();

        Assert.True(world.Redo());

        var restored = Assert.Single(world.WorldObjects.Live);
        var state = world.WorldObjects.Read(restored);
        Assert.Equal(address, state.Address);
        Assert.Equal(UserPut, state.Placement);
        Assert.False(state.Visible);
    }

    [Fact]
    public void Releasing_a_world_object_takes_an_entry_that_re_adopts()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(
            claim, new WorldObjectState(address, UserPut, true));

        world.Lifecycle.ReleaseWorldObject(claim);

        Assert.Equal("Remove world object", world.History.UndoDescription);
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));

        Assert.True(world.Undo());

        var restored = Assert.Single(world.WorldObjects.Live);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
    }

    [Fact]
    public void Every_entry_about_one_claim_shares_one_slot()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.Lifecycle.ReleaseWorldObject(claim);
        // Undoing the removal mints a NEW claim on the same address; undoing
        // past it must release THAT claim, not the corpse the adoption held.
        Assert.True(world.Undo());

        Assert.True(world.Undo());

        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));
    }

    [Fact]
    public void Releasing_every_claim_is_one_entry()
    {
        var world = new World();
        var first = world.WorldObjects.Place(0x1000, MapStood);
        var second = world.WorldObjects.Place(0x2000, MapStood);
        world.Lifecycle.AdoptWorldObject(first);
        world.Lifecycle.AdoptWorldObject(second);

        world.Lifecycle.ReleaseAllWorldObjects();

        Assert.Equal("Remove 2 world objects", world.History.UndoDescription);
        Assert.Empty(world.WorldObjects.Live);

        Assert.True(world.Undo());

        Assert.Equal(2, world.WorldObjects.Live.Count);
    }

    [Fact]
    public void An_adoption_the_world_refuses_takes_no_entry()
    {
        var world = new World();
        world.WorldObjects.Place(0x1000, MapStood);
        world.WorldObjects.RefuseAdopt = true;

        Assert.Null(world.Lifecycle.AdoptWorldObject(0x1000));

        Assert.Null(world.History.UndoDescription);
    }

    [Fact]
    public void A_re_adoption_the_world_refuses_leaves_the_entry_where_it_was()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        world.Lifecycle.AdoptWorldObject(address);
        world.Undo();
        world.WorldObjects.RefuseAdopt = true;

        Assert.False(world.Redo());

        Assert.Empty(world.WorldObjects.Live);
    }

    // ── the shared stack ─────────────────────────────────────────────────

    [Fact]
    public void Reconcile_never_drops_a_lifecycle_entry()
    {
        var world = new World();
        world.Lifecycle.SpawnLight(LightKind.Spot);

        // A lifecycle entry holds no target state, and the entity it names is
        // deliberately absent for half its life — the staleness rule that
        // prunes transform patches must not touch it.
        world.History.Reconcile(static _ => false);

        Assert.True(world.History.CanUndo);
    }

    /// <summary>
    /// A slot serves entries and nothing else, so it may not outlive them.
    /// Leaving GPose clears the history, and the slots go with it rather than
    /// keeping handles into a session that has ended.
    /// </summary>
    [Fact]
    public void Clearing_the_history_forgets_every_slot()
    {
        var world = new World();
        world.Lifecycle.SpawnLight(LightKind.Spot);
        world.Lifecycle.CreateCamera(CameraKind.Free);
        world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("A"));
        world.Lifecycle.SpawnProp(Apple);
        world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk);
        Assert.Single(Slots(world.Lifecycle, "_lightSlots"));

        world.History.Clear();

        Assert.Empty(Slots(world.Lifecycle, "_lightSlots"));
        Assert.Empty(Slots(world.Lifecycle, "_cameraSlots"));
        Assert.Empty(Slots(world.Lifecycle, "_actorSlots"));
        Assert.Empty(Slots(world.Lifecycle, "_propSlots"));
        Assert.Empty(Slots(world.Lifecycle, "_overlaySlots"));
    }

    /// <summary>
    /// Undo switched off empties the stacks on every append. That is the same
    /// emptying <see cref="TransformHistory.Clear"/> performs, so it drops the
    /// slots the same way — otherwise the one configuration that keeps no
    /// history at all would be the one that accumulated state behind it.
    /// </summary>
    [Fact]
    public void With_undo_switched_off_a_spawn_keeps_neither_entry_nor_slot()
    {
        var world = new World(capacity: 0);

        var light = world.Lifecycle.SpawnLight(LightKind.Spot);

        // The light is spawned either way: the act was never conditional on
        // being undoable.
        Assert.NotNull(light);
        Assert.Single(world.Lighting.Live);
        Assert.False(world.History.CanUndo);
        Assert.Empty(Slots(world.Lifecycle, "_lightSlots"));
    }

    private static IDictionary Slots(
        SceneLifecycleHistory lifecycle, string field) =>
        (IDictionary)typeof(SceneLifecycleHistory)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lifecycle)!;

    [Fact]
    public void Lifecycle_and_transform_entries_share_one_ordered_stack()
    {
        var world = new World();
        world.History.Append(new TransformPatch("edit", [], []));
        world.Lifecycle.SpawnLight(LightKind.Spot);

        Assert.Equal("Add spot light", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal("edit", world.History.UndoDescription);
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

        /// <param name="capacity">Undo depth; below 1 is undo switched off.
        /// </param>
        public World(int capacity = TransformHistory.DefaultCapacity)
        {
            History = new TransformHistory(() => capacity);
            Lifecycle = new SceneLifecycleHistory(
                History, Lighting, Cameras, Actors, Props, Overlays,
                WorldObjects);
        }

        public bool Undo()
        {
            var entry = (SceneLifecyclePatch)History.PeekUndo()!;
            if (!entry.Undo())
                return false;
            History.CommitUndo(entry);
            return true;
        }

        public bool Redo()
        {
            var entry = (SceneLifecyclePatch)History.PeekRedo()!;
            if (!entry.Redo())
                return false;
            History.CommitRedo(entry);
            return true;
        }
    }

    private sealed class FakeLighting : ILightingService
    {
        private readonly List<ILight> _lights = new();

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
        public ILight? CaptureWorldLight(WorldLightCandidate candidate) => null;
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
        private readonly List<IVirtualCamera> _cameras = new();

        public IReadOnlyList<IVirtualCamera> Live => _cameras;

        public bool IsAvailable => true;
        public IReadOnlyList<IVirtualCamera> Cameras => _cameras;
        public IVirtualCamera? LiveCamera => null;

        public FreeCameraSpeedNotice? SpeedNotice => null;
        public void ReportUiTextFocus(bool focused) { }
        public void Dispose() { }

        public IVirtualCamera? CreateCamera(CameraKind kind)
        {
            var camera = new FakeCamera(kind);
            _cameras.Add(camera);
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
            ((FakeCamera)camera).IsValid = false;
        }

        public void DestroyAllCameras() => _cameras.Clear();
        public void SetLive(IVirtualCamera camera) { }
        public bool SetTargetActor(
            IVirtualCamera camera, IActor actor, string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
    }

    private sealed class FakeCamera(CameraKind kind) : IVirtualCamera
    {
        public bool IsValid { get; set; } = true;
        public string Name { get; set; } = "Camera";
        public CameraKind Kind { get; } = kind;
        public bool IsLive => false;
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
        private readonly List<object> _adopted = new();

        public bool RefuseAdopt { get; set; }
        public IReadOnlyList<object> Live => _adopted;
        public IReadOnlyList<object> WorldObjects => _adopted.ToList();

        /// <summary>Where the map stands one address, which is what every
        /// release has to put back.</summary>
        public Transform MapPlacement(nint address) => _map[address];

        public nint Place(nint address, Transform placement)
        {
            _map[address] = placement;
            return address;
        }

        public object? Adopt(nint address)
        {
            if (RefuseAdopt || !_map.ContainsKey(address))
                return null;
            var claim = new FakeWorldObject
            {
                Owner = this,
                State = new WorldObjectState(address, _map[address], true),
                MapPlacement = _map[address],
            };
            _adopted.Add(claim);
            return claim;
        }

        public bool IsLive(object worldObject) =>
            ((FakeWorldObject)worldObject).IsValid;

        public void Release(object worldObject)
        {
            var claim = (FakeWorldObject)worldObject;
            _adopted.Remove(claim);
            if (claim.IsValid)
                _map[claim.State.Address] = claim.MapPlacement;
            claim.IsValid = false;
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

    private sealed class FakeProp
    {
        public bool IsValid { get; set; } = true;
        public PropState State { get; set; }
    }

    /// <summary>The overlay half at its port: a token per staged node holding
    /// the one document that IS its identity.</summary>
    private sealed class FakeOverlays : IOverlayLifecycle
    {
        private readonly List<object> _overlays = new();

        public bool RefuseCreate { get; set; }
        public IReadOnlyList<object> Live => _overlays;
        public IReadOnlyList<object> Overlays => _overlays.ToList();

        public object? Create(OverlayNodeState state)
        {
            if (RefuseCreate)
                return null;
            var overlay = new FakeOverlay
            {
                // The service names an unnamed document; the port fake says so
                // too, because the history's entry descriptions read the name
                // back out of the created node.
                State = state.Name.Length == 0
                    ? state with { Name = KindName(state.Kind) }
                    : state,
            };
            _overlays.Add(overlay);
            return overlay;
        }

        public bool IsLive(object overlay) => ((FakeOverlay)overlay).IsValid;

        public void Destroy(object overlay)
        {
            _overlays.Remove(overlay);
            ((FakeOverlay)overlay).IsValid = false;
        }

        /// <summary>The node leaves without this seam's knowledge — a scene
        /// import, or the game taking its UI down.</summary>
        public void VanishWithoutNotice(object overlay) => Destroy(overlay);

        public OverlayNodeState Read(object overlay) =>
            ((FakeOverlay)overlay).State;

        public void Write(object overlay, Func<OverlayNodeState, OverlayNodeState> edit) =>
            ((FakeOverlay)overlay).State = edit(((FakeOverlay)overlay).State);

        private static string KindName(OverlayNodeKind kind) => kind switch
        {
            OverlayNodeKind.Balloon => "Balloon",
            OverlayNodeKind.Status => "Status",
            _ => "Dialog",
        };
    }

    private sealed class FakeOverlay
    {
        public bool IsValid { get; set; } = true;
        public OverlayNodeState State { get; set; } = new();
    }

    private sealed class FakeActors : IActorLifecycle
    {
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

        public ActorState Read(object actor) => _states[(IActor)actor];

        public void Restore(object actor, ActorState state) =>
            _states[(IActor)actor] = state;

        public void Note(string detail) => Notes.Add(detail);
    }
}
