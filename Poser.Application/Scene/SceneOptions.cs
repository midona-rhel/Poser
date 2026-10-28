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
/// Categories and optional data to include in one save. Modded appearance is
/// excluded by default; enabling it requests embedded package bytes, not a
/// reference to a package on this computer.
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

    /// <summary>Saves each actor's (and attached companion's) animation and
    /// only the user's own bone edits, so a load plays on from that state,
    /// instead of only the baked pose. On by default; the save dialog can turn it off.</summary>
    public bool IncludeAnimation { get; init; } = true;

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

    /// <summary>Name for the saved group or single entity. Group members keep
    /// their individual names.</summary>
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
/// Categories, placement, and clear-first behavior for one load. Defaults
/// include every category and add the saved entities to the current scene.
/// Relative placement moves the content together to preserve its arrangement.
/// </summary>
public sealed record SceneLoadOptions
{
    /// <summary>
    /// Clears the current scene before creating loaded entities. Defaults to false.
    /// The clear is outside the load's undo and rollback; the outcome reports
    /// what was removed because those entities cannot be restored by undoing the load.
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

    /// <summary>Known current map for a UI default, resolved against the loaded document before placement.
    /// Zero means explicit placement; history retains the resolved result.</summary>
    public uint DefaultPlacementTerritory { get; init; }

    /// <summary>The current anchor pose the placement measures against,
    /// resolved by the caller at load start.</summary>
    public System.Numerics.Vector3 PlacementPosition { get; init; }
    public float PlacementYaw { get; init; }

    /// <summary>Additive loading of every category at its saved placement.</summary>
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
