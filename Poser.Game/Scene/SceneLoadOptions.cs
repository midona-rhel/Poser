using System;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Files;

namespace Poser.Game.Scene;

/// <summary>
/// What a scene SAVE is asked to put in the document. The six category flags
/// mirror the load's, so "what a scene contains" is one vocabulary in both
/// directions.
///
/// <para><see cref="IncludeModdedAppearance"/> is the one consent switch, and
/// it is off by default and never inferred: a scene saved with it on cannot
/// hand the next save its answer, because the actor's mods are somebody's
/// private data and each save is its own decision. On, it means PORTABLE —
/// the package's bytes go into the document. It never means "keep a path and
/// call it portable".</para>
/// </summary>
public sealed record SceneSaveOptions
{
    public bool IncludeActors { get; init; } = true;
    public bool IncludeProps { get; init; } = true;
    public bool IncludeLights { get; init; } = true;
    public bool IncludeCameras { get; init; } = true;
    public bool IncludeEnvironment { get; init; } = true;
    public bool IncludeOverlays { get; init; } = true;

    /// <summary>Embeds a portable modded-appearance package per actor. Off by
    /// default; consent is per save.</summary>
    public bool IncludeModdedAppearance { get; init; }

    /// <summary>
    /// Restricts the save to the one actor with this logical identity — the
    /// actor-library entry (.xiva) save. The capture still runs whole so it
    /// reads the same caches every save reads; the document is narrowed to
    /// this actor before appearance sealing, so only its package is read and
    /// embedded. A save that names an actor the capture did not contain
    /// refuses by name.
    /// </summary>
    public Guid? OnlyActorLogicalId { get; init; }

    /// <summary>The actor-entry save: one actor, its appearance embedded,
    /// nothing else.</summary>
    public static SceneSaveOptions ActorEntry(Guid logicalId) => new()
    {
        IncludeProps = false,
        IncludeLights = false,
        IncludeCameras = false,
        IncludeEnvironment = false,
        IncludeOverlays = false,
        IncludeModdedAppearance = true,
        IncludeStructure = false,
        OnlyActorLogicalId = logicalId,
    };

    /// <summary>Whether the document carries the sidebar's structure —
    /// named groups and the user's root order. On for whole-scene saves;
    /// single-entity entries have no structure to say.</summary>
    public bool IncludeStructure { get; init; } = true;

    /// <summary>Restricts the save to the entities whose keys (logical
    /// ids; actor capture keys are admitted by translation) are in the
    /// set — the group-entry save. Groups survive only when every member
    /// is kept.</summary>
    public IReadOnlyCollection<Guid>? OnlyEntityKeys { get; init; }

    /// <summary>The group-entry save: one named group's members with
    /// their appearances, the group riding along so the load recreates
    /// it whole.</summary>
    public static SceneSaveOptions GroupEntry(
        IReadOnlyCollection<Guid> memberKeys) => new()
    {
        IncludeEnvironment = false,
        IncludeModdedAppearance = true,
        OnlyEntityKeys = memberKeys,
    };

    /// <summary>Marks every saved world object as SPAWNED — the
    /// world-object entry's rewrite: a borrowed original becomes a
    /// spawnable copy on disk.</summary>
    public bool WorldObjectsAsSpawned { get; init; }

    /// <summary>The name the save modal took: it lands ON the entry's one
    /// thing — Stone rail spawns a Stone rail. A group entry names the
    /// GROUP; children keep their own saved names.</summary>
    public string? EntryName { get; init; }

    /// <summary>The light-entry save: one light, nothing else — the same
    /// container and key filter every entry uses.</summary>
    public static SceneSaveOptions LightEntry(Guid key) => new()
    {
        IncludeActors = false,
        IncludeProps = false,
        IncludeCameras = false,
        IncludeEnvironment = false,
        IncludeOverlays = false,
        IncludeStructure = false,
        OnlyEntityKeys = new[] { key },
    };

    /// <summary>The camera-entry save: one camera, nothing else.</summary>
    public static SceneSaveOptions CameraEntry(Guid key) => new()
    {
        IncludeActors = false,
        IncludeProps = false,
        IncludeLights = false,
        IncludeEnvironment = false,
        IncludeOverlays = false,
        IncludeStructure = false,
        OnlyEntityKeys = new[] { key },
    };

    /// <summary>The world-object-entry save: one object as a spawnable
    /// copy — path and placement, no map identity to match. Overlays stay
    /// INCLUDED even though none survive the key prune: the save policy
    /// couples <c>scene.WorldObjects</c> to the overlays flag, and setting
    /// it false shipped empty entries.</summary>
    public static SceneSaveOptions WorldObjectEntry(Guid key) => new()
    {
        IncludeActors = false,
        IncludeProps = false,
        IncludeLights = false,
        IncludeCameras = false,
        IncludeEnvironment = false,
        IncludeStructure = false,
        OnlyEntityKeys = new[] { key },
        WorldObjectsAsSpawned = true,
    };

    /// <summary>Restricts the save to one overlay — the overlay-entry
    /// (.xivo) save. Same contract as the actor filter.</summary>
    public Guid? OnlyOverlayKey { get; init; }

    /// <summary>The prop-entry save: one spawned prop — model, dyes,
    /// pose variant, placement — nothing else.</summary>
    public static SceneSaveOptions PropEntry(Guid key) => new()
    {
        IncludeActors = false,
        IncludeLights = false,
        IncludeCameras = false,
        IncludeEnvironment = false,
        IncludeOverlays = false,
        IncludeStructure = false,
        OnlyEntityKeys = new[] { key },
    };

    /// <summary>The overlay-entry save: one overlay node, nothing else.
    /// </summary>
    public static SceneSaveOptions OverlayEntry(Guid key) => new()
    {
        IncludeActors = false,
        IncludeProps = false,
        IncludeLights = false,
        IncludeCameras = false,
        IncludeEnvironment = false,
        IncludeStructure = false,
        OnlyOverlayKey = key,
    };

    /// <summary>The environment-entry save: the environment configuration
    /// and nothing else.</summary>
    public static SceneSaveOptions EnvironmentEntry { get; } = new()
    {
        IncludeActors = false,
        IncludeProps = false,
        IncludeLights = false,
        IncludeCameras = false,
        IncludeOverlays = false,
        IncludeStructure = false,
    };

    public static SceneSaveOptions Default { get; } = new();
}

/// <summary>
/// What a scene load is asked to do with the document it read. Every member's
/// DEFAULT is the behaviour the load had before options existed, so
/// <see cref="Default"/> and no options at all are the same load.
///
/// <para>The set is the union of both references' import options, narrowed to
/// what Poser's own loader can actually honour: Brio's <c>Override Current
/// Scene</c> plus its six category flags plus its two relative-position
/// toggles (<c>UI/Controls/Stateless/FileUIHelpers.cs</c>,
/// <c>Services/SceneService.cs ImportScene</c>), and Ktisis's five per-category
/// load checkboxes, its <c>Keep existing actors</c> opt-out and its
/// world-space/local-space toggle
/// (<c>Interface/Windows/Editors/SceneWindow.cs</c>,
/// <c>Services/Data/SceneDataService.cs Load</c>). Brio's <c>Folders</c>
/// category has no Poser counterpart — Poser's tree has fixed sections — and
/// is deliberately absent rather than present and inert.</para>
///
/// <para>The two references split the relative choice per category (Brio has
/// separate light and world-object toggles); Poser states ONE choice, because
/// a scene half-rebased is a scene whose entities no longer stand where they
/// stood beside each other.</para>
/// </summary>
public sealed record SceneLoadOptions
{
    /// <summary>
    /// Clear what the session is holding before restoring anything. FALSE is
    /// Poser's own long-standing behaviour — a load is additive — and is kept
    /// as the default even though BOTH references default the other way (Brio
    /// destroys unless <c>SceneDestoryActorsBeforeImport</c> is off; Ktisis
    /// destroys unless <c>Keep existing actors</c> is ticked).
    ///
    /// <para>The clear is NOT part of the load's transaction: rollback restores
    /// what the load CREATED, and nothing can resurrect an actor the user asked
    /// to be rid of. A load that clears therefore says so in its outcome.</para>
    /// </summary>
    public bool ClearExistingScene { get; init; }

    public bool IncludeActors { get; init; } = true;
    public bool IncludeProps { get; init; } = true;
    public bool IncludeLights { get; init; } = true;
    public bool IncludeCameras { get; init; } = true;
    public bool IncludeEnvironment { get; init; } = true;
    public bool IncludeOverlays { get; init; } = true;

    /// <summary>
    /// Place the scene relative to where the user is standing NOW rather than
    /// where it was captured: every stated world position moves by
    /// (current origin − <see cref="SceneFile.Origin"/>). Requires the document
    /// to carry an origin; a file that does not is refused BY NAME rather than
    /// rebased onto a guess.
    /// </summary>
    public bool PlaceRelativeToCurrentOrigin { get; init; }

    /// <summary>Where the loaded content lands: the object-entry placement
    /// (a library actor tile), resolved by the CALLER against the live
    /// session. AsSaved is every ordinary load.</summary>
    public ObjectPlacementMode Placement { get; init; }

    /// <summary>The current anchor pose the placement measures against,
    /// resolved by the caller at load start.</summary>
    public System.Numerics.Vector3 PlacementPosition { get; init; }
    public float PlacementYaw { get; init; }

    /// <summary>Today's load, stated once.</summary>
    public static SceneLoadOptions Default { get; } = new();

    /// <summary>Whether any category at all is asked for. A load that includes
    /// nothing is refused at admission — it would report success over an
    /// untouched session.</summary>
    public bool IncludesAnything =>
        IncludeActors || IncludeProps || IncludeLights ||
        IncludeCameras || IncludeEnvironment || IncludeOverlays;
}

/// <summary>
/// The relative-placement rebase: one pure pass over a READ document that
/// moves every world position it states by a single offset.
///
/// <para>It runs on the deserialized document before any native work, which is
/// why the wire format stays absolute — the alternative (Ktisis's, which writes
/// positions already relative to a saved origin) makes every number in the file
/// meaningless without the origin beside it, and Poser's listings, diffs and
/// codecs all read the file without one.</para>
///
/// <para>What moves is everything whose position is a point IN the scene:
/// actor placements, prop transforms, world-space lights, free-camera
/// positions, and the gaze world points an actor is looking at. What does NOT
/// move is anything already expressed relative to something that moved with
/// the scene — a bone-attached light (its position is the bone's), an orbit
/// camera (it orbits its target), and a camera's target offset.</para>
///
/// <para>A BORROWED MAP OBJECT also does not move, and for a different reason
/// than either: it is not Poser's to place. Its identity IS the point the map
/// stands it at, so rebasing it would match the fixture at its real spot and
/// then shove it by an arbitrary offset — a pillar hanging over a field. A
/// relative load therefore leaves borrowed objects where the map has them and
/// says so, rather than dragging the zone's own furniture along.</para>
/// </summary>
public static class SceneRelativePlacement
{
    /// <summary>
    /// Rebases <paramref name="scene"/> onto <paramref name="currentOrigin"/>
    /// in place. Returns null when it landed, else the named refusal — the
    /// document states no origin, so there is nothing to rebase FROM.
    /// </summary>
    public static string? Rebase(SceneFile scene, Vector3 currentOrigin)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.Origin is not { } saved)
        {
            return "The scene records no origin, so it cannot be placed " +
                "relative to where you are standing. Load it as saved instead.";
        }
        if (!float.IsFinite(currentOrigin.X) ||
            !float.IsFinite(currentOrigin.Y) ||
            !float.IsFinite(currentOrigin.Z))
        {
            return "The current position could not be read, so the scene " +
                "cannot be placed relative to it.";
        }

        var offset = currentOrigin - saved;
        if (offset == Vector3.Zero)
            return null;

        foreach (var actor in scene.Actors)
        {
            if (actor.ModelTransform is { } placement)
                placement.Position += offset;
            // The gaze's world points are points in THIS scene: an actor
            // looking at a spot on the floor must keep looking at the same spot
            // on the moved floor. They are moved whatever the mode says,
            // because a per-part lock can pin a point while the mode reads
            // Camera or Entity.
            if (actor.Gaze is { } gaze)
            {
                gaze.Position += offset;
                gaze.EyesPosition += offset;
                gaze.HeadPosition += offset;
                gaze.BodyPosition += offset;
            }
        }

        foreach (var prop in scene.Props)
            prop.Transform.Position += offset;

        // Only what POSER spawned moves; the map's own objects are matched
        // by where the map stands them.
        foreach (var worldObject in scene.WorldObjects ?? [])
            if (worldObject.Spawned)
                worldObject.Transform.Position += offset;

        foreach (var light in scene.Lights)
        {
            // An attached light's transform is stated against its bone, and
            // the bone moved with its actor already.
            if (light.Attachment is not null)
                continue;
            if (light.Light is { } document)
                document.Transform.Position += offset;
        }

        foreach (var camera in scene.Cameras)
        {
            // Only a free camera states a world position; an orbit camera is
            // angle and zoom about a target that moved with the scene, and its
            // TargetOffset is relative to that target either way.
            if (camera.Camera is { Kind: CameraKind.Free } document)
                document.Position += offset;
        }

        return null;
    }
}

/// <summary>
/// The object-entry placement rebase: the origin rebase's sibling, with the
/// turn the origin rebase deliberately lacks. Everything whose position is a
/// point IN the document moves to the current anchor and turns by the yaw
/// difference; rotations turn with it, keeping their own pitch and roll.
/// Borrowed map objects do not move, for the origin rebase's own reason.
/// </summary>
public static class ScenePlacementRebase
{
    public static string? Rebase(
        SceneFile scene,
        Poser.Files.PlacementAnchorData saved,
        Vector3 currentPosition,
        float currentYaw)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(currentPosition.X) ||
            !float.IsFinite(currentPosition.Y) ||
            !float.IsFinite(currentPosition.Z) ||
            !float.IsFinite(currentYaw))
            return "The current anchor could not be read, so the entry " +
                "cannot be placed relative to it.";

        float yawDelta = currentYaw - saved.Yaw;
        var turn = System.Numerics.Quaternion.CreateFromAxisAngle(
            Vector3.UnitY, yawDelta);
        Vector3 Move(Vector3 point) => currentPosition +
            Vector3.Transform(point - saved.Position, turn);

        foreach (var actor in scene.Actors)
        {
            if (actor.ModelTransform is { } placement)
            {
                placement.Position = Move(placement.Position);
                placement.Rotation = System.Numerics.Quaternion.Normalize(
                    turn * placement.Rotation);
            }
            if (actor.Gaze is { } gaze)
            {
                gaze.Position = Move(gaze.Position);
                gaze.EyesPosition = Move(gaze.EyesPosition);
                gaze.HeadPosition = Move(gaze.HeadPosition);
                gaze.BodyPosition = Move(gaze.BodyPosition);
            }
        }
        foreach (var prop in scene.Props)
        {
            prop.Transform.Position = Move(prop.Transform.Position);
            prop.Transform.Rotation = System.Numerics.Quaternion.Normalize(
                turn * prop.Transform.Rotation);
        }
        // Only what POSER spawned moves; the map's own objects are matched
        // by where the map stands them.
        foreach (var worldObject in scene.WorldObjects ?? [])
        {
            if (!worldObject.Spawned)
                continue;
            worldObject.Transform.Position =
                Move(worldObject.Transform.Position);
            worldObject.Transform.Rotation =
                System.Numerics.Quaternion.Normalize(
                    turn * worldObject.Transform.Rotation);
        }
        foreach (var light in scene.Lights)
        {
            if (light.Attachment is not null)
                continue;
            if (light.Light is { } document)
            {
                document.Transform.Position =
                    Move(document.Transform.Position);
                document.Transform.Rotation =
                    System.Numerics.Quaternion.Normalize(
                        turn * document.Transform.Rotation);
            }
        }
        foreach (var camera in scene.Cameras)
        {
            if (camera.Camera is { Kind: CameraKind.Free } document)
            {
                document.Position = Move(document.Position);
                document.Angle = document.Angle with
                {
                    X = document.Angle.X + yawDelta,
                };
            }
        }
        return null;
    }
}
