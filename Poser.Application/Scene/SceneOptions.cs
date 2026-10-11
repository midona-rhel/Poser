using System.Collections.Generic;
using System;
using System.Numerics;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>The categories a scene document is made of — one vocabulary for
/// what a save keeps and what a load restores.</summary>
[Flags]
public enum SceneCategories
{
    None = 0,
    Actors = 1 << 0,
    Props = 1 << 1,
    Lights = 1 << 2,
    Cameras = 1 << 3,
    Environment = 1 << 4,
    Overlays = 1 << 5,
    WorldObjects = 1 << 6,
    All = Actors | Props | Lights | Cameras | Environment | Overlays | WorldObjects,
}

/// <summary>
/// What a scene SAVE is asked to put in the document. The category flags
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
    public bool IncludeWorldObjects { get; init; } = true;

    /// <summary>Embeds a portable modded-appearance package per actor. Off by
    /// default; consent is per save.</summary>
    public bool IncludeModdedAppearance { get; init; }

    /// <summary>Whether the document carries the sidebar's structure —
    /// named groups and the user's root order. On for whole-scene saves;
    /// single-entity entries have no structure to say.</summary>
    public bool IncludeStructure { get; init; } = true;

    /// <summary>Restricts the save to the entities whose keys (logical
    /// ids; actor capture keys are admitted by translation) are in the
    /// set — every library entry save. Groups survive only when every
    /// member is kept; references to anything pruned are detached by
    /// <see cref="SceneSaveNarrowing"/>.</summary>
    public IReadOnlyCollection<Guid>? OnlyEntityKeys { get; init; }

    /// <summary>The name the save modal took: it lands ON the entry's one
    /// thing — Stone rail spawns a Stone rail. A group entry names the
    /// GROUP; children keep their own saved names.</summary>
    public string? EntryName { get; init; }

    /// <summary>An entry save: only these categories, narrowed to these
    /// keys when any are given, with no sidebar structure.</summary>
    public static SceneSaveOptions Only(
        SceneCategories categories, IReadOnlyCollection<Guid>? keys = null) => new()
    {
        IncludeActors = categories.HasFlag(SceneCategories.Actors),
        IncludeProps = categories.HasFlag(SceneCategories.Props),
        IncludeLights = categories.HasFlag(SceneCategories.Lights),
        IncludeCameras = categories.HasFlag(SceneCategories.Cameras),
        IncludeEnvironment = categories.HasFlag(SceneCategories.Environment),
        IncludeOverlays = categories.HasFlag(SceneCategories.Overlays),
        IncludeWorldObjects = categories.HasFlag(SceneCategories.WorldObjects),
        IncludeStructure = false,
        OnlyEntityKeys = keys,
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
    public bool IncludeWorldObjects { get; init; } = true;

    /// <summary>
    /// Place the scene relative to where the user is standing NOW rather than
    /// where it was captured: every stated world position moves by
    /// (current origin − the saved origin). Requires the document
    /// to carry an origin; a file that does not is refused BY NAME rather than
    /// rebased onto a guess.
    /// </summary>
    public bool PlaceRelativeToCurrentOrigin { get; init; }

    /// <summary>Where the loaded content lands: the object-entry placement
    /// (a library actor tile), resolved by the CALLER against the live
    /// session. AsSaved is every ordinary load. Any other mode wins over
    /// <see cref="PlaceRelativeToCurrentOrigin"/>: the two are exclusive,
    /// so content lands once.</summary>
    public ObjectPlacementMode Placement { get; init; }

    /// <summary>The current anchor pose the placement measures against,
    /// resolved by the caller at load start.</summary>
    public System.Numerics.Vector3 PlacementPosition { get; init; }
    public float PlacementYaw { get; init; }

    /// <summary>Today's load, stated once.</summary>
    public static SceneLoadOptions Default { get; } = new();

    /// <summary>An additive load of only these categories.</summary>
    public static SceneLoadOptions Only(SceneCategories categories) => new()
    {
        IncludeActors = categories.HasFlag(SceneCategories.Actors),
        IncludeProps = categories.HasFlag(SceneCategories.Props),
        IncludeLights = categories.HasFlag(SceneCategories.Lights),
        IncludeCameras = categories.HasFlag(SceneCategories.Cameras),
        IncludeEnvironment = categories.HasFlag(SceneCategories.Environment),
        IncludeOverlays = categories.HasFlag(SceneCategories.Overlays),
        IncludeWorldObjects = categories.HasFlag(SceneCategories.WorldObjects),
    };

    /// <summary>Whether any category at all is asked for. A load that includes
    /// nothing is refused at admission — it would report success over an
    /// untouched session.</summary>
    public bool IncludesAnything =>
        IncludeActors || IncludeProps || IncludeLights ||
        IncludeCameras || IncludeEnvironment || IncludeOverlays ||
        IncludeWorldObjects;
}
