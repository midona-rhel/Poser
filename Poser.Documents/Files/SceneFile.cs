using Poser.Domain.Operations;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Domain.Scene;

namespace Poser.Documents.Files;

/// <summary>
/// A Poser scene file (.xivs). It is one JSON document that holds everything
/// in a scene: the actors and their poses, the props, lights and cameras, the
/// environment, and how they are linked (a companion on an actor, a light
/// attached to a bone, a camera following an actor).
///
/// Each actor's pose is stored as a whole <see cref="PoseFile"/>, and each
/// light and camera as a whole <see cref="LightFile"/> or
/// <see cref="CameraFile"/>. So a light saved in a scene is exactly the light
/// saved on its own as a .xivl.
///
/// <see cref="SceneId"/> stays the same every time the same scene is saved.
/// Loading a scene reports its result under that id, because a whole scene
/// has no single actor to report under.
/// </summary>
[Serializable]
public class SceneFile
{
    /// <summary>The scene format version this build writes. Raise it for any
    /// change an older Poser would lose or misread. Files from
    /// <see cref="MinimumVersion"/> up to this version are read, and newer
    /// files are refused.</summary>
    public const int CurrentVersion = 4;

    /// <summary>The oldest scene format this build still reads.</summary>
    public const int MinimumVersion = 2;

    /// <summary>The extension of a scene file.</summary>
    public const string Extension = ".xivs";

    /// <summary>
    /// An actor saved to the library: a scene file that holds one actor and
    /// nothing else. Its own extension lets the library put it in the right
    /// tab without opening it. Everything else treats it as a scene.
    /// </summary>
    public const string ActorEntryExtension = ".xiva";

    /// <summary>Where the camera and the selected actor stood when saving, and
    /// which way they faced along the ground. Every save records them. Loading
    /// a library entry relative to the camera or to an actor uses them.</summary>
    public PlacementAnchorData? CameraAnchor { get; set; }
    public PlacementAnchorData? ActorAnchor { get; set; }

    /// <summary>
    /// An environment saved to the library: a scene file that holds only the
    /// environment settings (weather, sky, atmosphere, world rendering). Its
    /// own extension lets the library put it in the right tab without opening
    /// it.
    /// </summary>
    public const string EnvironmentEntryExtension = ".xive";

    /// <summary>
    /// An overlay saved to the library: a scene file that holds one overlay.
    /// </summary>
    public const string OverlayEntryExtension = ".xivo";

    /// <summary>
    /// A group saved to the library: a scene file that holds one group and
    /// its members, so loading it recreates the whole group. Its own extension
    /// lets the library put it in the right tab without opening it.
    /// </summary>
    public const string GroupEntryExtension = ".xivg";

    /// <summary>
    /// A world object saved to the library: a scene file that holds one world
    /// object, saved as a copy Poser spawns. Loading it creates the object
    /// anywhere, in any zone.
    /// </summary>
    public const string WorldObjectEntryExtension = ".xivw";

    /// <summary>A prop saved to the library: a scene file that holds one
    /// weapon-model prop with its model, dyes and pose variant.</summary>
    public const string PropEntryExtension = ".xivp";

    /// <summary>
    /// A light saved to the library: a scene file that holds one light, saved
    /// and loaded the same way as a scene. Older .xivl files hold only the
    /// light's own JSON; they are still read (see <see cref="SceneFileStore"/>),
    /// but Poser writes only the scene form.
    /// </summary>
    public const string LightEntryExtension = ".xivl";

    /// <summary>A camera saved to the library, stored the same way as a light
    /// (<see cref="LightEntryExtension"/>).
    /// </summary>
    public const string CameraEntryExtension = ".xivc";

    public string TypeName { get; set; } = "XIV Scene";
    public int FileVersion { get; set; } = CurrentVersion;

    /// <summary>The scene's id. It stays the same across saves and is never
    /// empty in a valid file.</summary>
    public Guid SceneId { get; set; }

    /// <summary>The name Poser gives an actor's scene appearance when it packs it while
    /// saving. Older scenes kept this name when an earlier load had applied it as an MCDF.</summary>
    public const string PackedAppearancePrefix = "poser-scene-appearance-";

    /// <summary>True when the actor's saved look is an MCDF that was loaded onto the actor,
    /// false when it is the actor's scene appearance or when nothing is saved. Older scenes
    /// did not record it: a look named after its actor, or named like Poser's own packing,
    /// counts as a scene appearance there.</summary>
    public bool HasLoadedMcdf(SceneActor actor) => actor.Mcdf is { } packed
        && (SavedBy is not null
            ? packed.LoadedOnto
            : !string.Equals(packed.FileName, actor.Name + ".mcdf", StringComparison.OrdinalIgnoreCase)
                && !packed.FileName.StartsWith(PackedAppearancePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Poser install that saved the scene. Absent in older scenes. Only
    /// the install that saved a scene loads its scene appearances editable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SavedBy { get; set; }

    public string? Author { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? SavedAt { get; set; }

    /// <summary>The zone the scene was saved in. 0 means unknown, as in files
    /// saved before the zone was recorded.
    /// </summary>
    public uint TerritoryId { get; set; }

    /// <summary>The zone's name, looked up when saving. It is stored next to
    /// the id because the library lists scenes without the game data needed
    /// to look a name up. Absent in older files and when the zone had no
    /// name.</summary>
    public string? PlaceName { get; set; }

    /// <summary>
    /// Where the player's character stood when the scene was saved. A
    /// relative load (placing the scene around where the character stands
    /// now) moves everything by the difference. Every other position in the
    /// file stays an absolute world position, so a reader that ignores this
    /// field still places everything correctly.
    ///
    /// <para>Brio does the same: it saves absolute positions and anchors on
    /// the player's character at load. Ktisis saves positions relative to an
    /// origin instead. Poser keeps absolute positions so the file still makes
    /// sense to anything that reads it without a relative load.</para>
    ///
    /// <para>Absent when there was no player character to anchor on, and in
    /// older files. A relative load of such a file is refused with a message
    /// rather than guessing.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Vector3? Origin { get; set; }

    public List<SceneActor> Actors { get; set; } = new();
    public List<SceneProp> Props { get; set; } = new();
    public List<SceneLight> Lights { get; set; } = new();
    public List<SceneCamera> Cameras { get; set; } = new();
    public SceneEnvironment? Environment { get; set; }

    /// <summary>Scene-wide switches that freeze water and physics. Absent when
    /// both are off, which is the game's normal behaviour.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneWorld? World { get; set; }

    /// <summary>The game-interface overlays placed in the scene. Absent when
    /// there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneOverlay>? Overlays { get; set; }

    /// <summary>Objects that belong to the map and that the scene took over.
    /// Absent when there are none.
    ///
    /// <para>These are the only part of a scene that only makes sense in the
    /// zone it was saved in. A map object has no id that lasts from one game
    /// session to the next, so each entry names it by its model path and the
    /// point where the map places it. Loading the scene in another zone skips
    /// every entry and names each one, rather than taking over some other
    /// object that uses the same model.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneWorldObject>? WorldObjects { get; set; }

    /// <summary>The named groups in the sidebar. Absent when there are none.
    /// Members point at entities by the same keys the lists above use. A
    /// member the load cannot bring back is skipped and named, and a group
    /// left with fewer than two members is removed, just as it is in the
    /// sidebar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneGroupEntry>? Groups { get; set; }

    /// <summary>The order of the sidebar's top level as it was arranged, with
    /// kinds mixed and groups included (Kind "group", by the group's key).
    /// Absent when not saved; a load then lists entities by kind.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneStructureRef>? RootOrder { get; set; }

    /// <summary>Parent links between saved entities, ABSENT when the scene
    /// has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneParentLink>? Parents { get; set; }

    // The JSON style every Poser file uses: vectors as comma-separated
    // strings, enums by name, PascalCase names, indented, text not escaped
    // more than needed, and trailing commas and unknown fields allowed.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        MaxDepth = SceneFileLimits.MaxJsonDepth,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
            new Converters.Vector2Converter(),
            new Converters.Vector3Converter(),
            new Converters.Vector4Converter(),
            new Converters.QuaternionConverter()
        }
    };
}

/// <summary>Size limits every scene read and write checks.</summary>
public static class SceneFileLimits
{
    /// <summary>
    /// The limit on the scene's JSON, which holds one pose per actor and no
    /// appearance files. It is twice the limit for a pose file.
    ///
    /// <para>It does not limit the whole file: appearances are stored as
    /// separate entries, so a scene with large appearances still has a small
    /// JSON.</para>
    /// </summary>
    public const long MaxDocumentBytes = 64L * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxNameCharacters = 256;

    /// <summary>The scene's description is free text, not a name, so it gets
    /// more room.</summary>
    public const int MaxDescriptionCharacters = 4096;

    /// <summary>The limit for file paths, which can be much longer than a
    /// name: a long-path prefix plus a deep library folder.</summary>
    public const int MaxPathCharacters = 1024;

    /// <summary>Hex characters of a SHA-256 digest.</summary>
    public const int ContentHashCharacters = 64;

    /// <summary>
    /// The limit on one actor's appearance saved in a scene. It is the same as the limit
    /// for importing an MCDF, so any MCDF Poser can import can also be saved
    /// in a scene. Real ones can be hundreds of megabytes.
    ///
    /// <para>Appearances are stored as separate entries and copied in pieces,
    /// never held in memory whole, so this limits disk use, not
    /// memory.</para>
    /// </summary>
    public const long MaxEmbeddedAppearanceBytes = 512L * 1024 * 1024;

    /// <summary>Above this size, a save still writes the appearance and says
    /// how large the file became. It is only a warning, never a reason to
    /// refuse the save.</summary>
    public const long LargeAppearanceWarningBytes = 256L * 1024 * 1024;
    public const float MinQuaternionLengthSquared =
        PoseFileLimits.MinQuaternionLengthSquared;
}

/// <summary>
/// One saved actor: its key in the file, what is needed to spawn it again,
/// its companion, where it stands, what it plays, where it looks, its whole
/// pose, its Penumbra collection and, if saved, its appearance.
/// </summary>
[Serializable]
public class SceneActor
{
    /// <summary>The actor's id within this file; links between entities use
    /// it. It has nothing to do with the actor's id in the game.</summary>
    public Guid Key { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Hash of the original character name, used to find the character in GPose without saving the name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityHash { get; set; }

    /// <summary>True when <see cref="Name"/> is the name as shown. Older files left it
    /// false and stored the game's name with a temporary number on the end.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NameIsDisplayName { get; set; }

    /// <summary>The game's model id for the actor (its ModelChara row); 0 is a normal
    /// human.</summary>
    public int ModelCharaId { get; set; }

    /// <summary>The catalog kind of a minion, mount or ornament spawned as an
    /// actor of its own. Absent for a character, and in files written before
    /// the kind was saved, which load as characters exactly as they did.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompanionKind? SpawnedKind { get; set; }

    /// <summary>The actor was the player's own character. A load puts the
    /// saved state back on the player's character instead of spawning a copy.
    /// Absent in older files, which spawn every actor.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LocalPlayer { get; set; }

    /// <summary>The actor had an appearance of its own that this file does not
    /// hold, so it loads looking like the local player.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AppearanceNotSaved { get; set; }

    /// <summary>A permanent Penumbra collection on this computer, set before
    /// posing. Temporary collections are kept in the actor's saved appearance instead.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PenumbraCollection { get; set; }

    public bool Visible { get; set; } = true;

    public bool ClothingOnly { get; set; }

    /// <summary>Whether the actor was spawned with room for a companion. A
    /// companion can only be restored when it was.</summary>
    public bool HasCompanionSlot { get; set; }

    /// <summary>The attached minion, mount or ornament. Absent when there is
    /// none.</summary>
    public CompanionKind? CompanionKind { get; set; }

    /// <summary>The companion's row id in the game data; 0 when
    /// <see cref="CompanionKind"/> is absent.</summary>
    public ushort CompanionId { get; set; }

    /// <summary>
    /// The attached companion's own pose. A minion, mount or ornament has a
    /// skeleton and can be posed like any actor; restoring only the companion
    /// would bring it back in its idle animation. Brio saves the same thing;
    /// Ktisis does not save companions.
    ///
    /// <para>Absent when there is no companion, when it had no skeleton to
    /// read, or when it was never posed.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PoseFile? CompanionPose { get; set; }

    /// <summary>The actor's whole pose, in Brio's format, checked by the same
    /// rules as a pose file. Required: a scene actor without a pose is not
    /// valid.</summary>
    public PoseFile? Pose { get; set; }

    /// <summary>
    /// Where the actor stands: the world position, rotation and scale of its
    /// model.
    ///
    /// <para>The pose holds a model transform too, but there a position of
    /// zero, no rotation and a scale of zero means "not recorded". An actor
    /// really standing at the world origin would look unrecorded and not be
    /// placed. This field has no such problem: when it is present, the actor
    /// is placed there.</para>
    ///
    /// <para>Absent in older files, which fall back to the pose's model
    /// transform.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LightFile.TransformData? ModelTransform { get; set; }

    /// <summary>Where the actor is looking. Absent when no gaze is set, which
    /// is usual.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneActorGaze? Gaze { get; set; }

    /// <summary>FABRIK chains saved by older versions. The pose already holds
    /// their result, so a load ignores them and shows a note instead of
    /// running the solver again. The data is kept as it is, never checked,
    /// and nothing writes it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Fabrik { get; set; }

    /// <summary>The appearance saved for the actor: an MCDF that was applied to
    /// it, or its scene appearance. Absent when no appearance was
    /// saved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneActorMcdf? Mcdf { get; set; }

    /// <summary>
    /// Saved when the save dialog's Animation option is on (format 4): what
    /// the actor was playing, layer by layer. A load then restores this
    /// animation with <see cref="PoseEdits"/> on top, instead of applying the
    /// still <see cref="Pose"/>, so the actor keeps moving. <see cref="Pose"/>
    /// is still written: a load uses it when the animation cannot be restored.
    /// Absent: the scene holds only the still pose.
    /// </summary>
    public SceneAnimationState? Animation { get; set; }

    /// <summary>The bone edits made on top of the animation, stored as
    /// offsets (only the edits, not the animation frame under them). Saved
    /// with <see cref="Animation"/>.</summary>
    public List<SceneBoneEdit>? PoseEdits { get; set; }

    /// <summary>The attached companion's (mount, minion, ornament) animation, with
    /// <see cref="Animation"/>.</summary>
    public SceneAnimationState? CompanionAnimation { get; set; }

    /// <summary>The attached companion's bone edits, with <see cref="CompanionAnimation"/>.</summary>
    public List<SceneBoneEdit>? CompanionPoseEdits { get; set; }
}

/// <summary>An actor's animation as a scene saves it: the same state an undo
/// step restores. A held layer keeps its frame; a running one carries on from
/// where it was. Animation ids are looked up in the game and mods of the
/// computer that loads the scene.</summary>
[Serializable]
public class SceneAnimationState
{
    public SceneAnimationLayer? Base { get; set; }
    public SceneAnimationLayer? UpperBody { get; set; }
    public SceneAnimationLayer? Facial { get; set; }
    public SceneAnimationLayer? Additive { get; set; }
    public ushort? Lips { get; set; }
    /// <summary>The whole-actor speed Poser holds (0 = paused), or none.</summary>
    public float? OverallSpeed { get; set; }
    public ushort? HeldExpression { get; set; }
    public AnimationStance Stance { get; set; }
    public int Pose { get; set; }
    public bool WeaponDrawn { get; set; }
    public bool PositionLock { get; set; }
    public bool Advanced { get; set; }
}

/// <summary>One layer of a <see cref="SceneAnimationState"/>.</summary>
public class SceneAnimationLayer
{
    /// <summary>The ActionTimeline row Poser played on the layer.</summary>
    public ushort Timeline { get; set; }
    public bool Playing { get; set; }
    public bool Loop { get; set; }
    public float? Speed { get; set; }
    public float Weight { get; set; } = 1f;
    /// <summary>The held frame, in seconds from the animation's start; absent
    /// while it runs.</summary>
    public float? HeldSeconds { get; set; }
    /// <summary>Set when the animation played to its end and stopped: its
    /// length in seconds. The layer stays.</summary>
    public float? EndedSeconds { get; set; }

    /// <summary>How far a running animation had played, in seconds; a load
    /// carries on from there. Absent in older files, which restart it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? PlayedSeconds { get; set; }
}

/// <summary>One bone's own edits: offsets the animation's frame is moved by.</summary>
public class SceneBoneEdit
{
    public PoseSlot Slot { get; set; }
    public int Partial { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<SceneBoneOffset> Offsets { get; set; } = new();
    public Vector3? PartialRootScale { get; set; }
}

/// <summary>One offset of a <see cref="SceneBoneEdit"/>.</summary>
public class SceneBoneOffset
{
    public TransformComponents Propagate { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; }
    public TransformFrame Frame { get; set; }
    public Vector3? IkPosition { get; set; }
    public Quaternion? IkRotation { get; set; }
    public Vector3? IkScale { get; set; }
}

/// <summary>
/// The appearance saved for an actor, in one of two ways.
///
/// <para>By reference (<see cref="PackageEntry"/> absent): the file stores
/// where the MCDF was, and a load imports it from there again. This only
/// works on the computer that saved the scene.</para>
///
/// <para>Portable (<see cref="PackageEntry"/> present): the scene file holds
/// the MCDF itself, so it works on any computer. A path or a temporary
/// collection means nothing on another computer, so a portable save either
/// includes the MCDF or saves the actor without it and says so. It never
/// falls back to a reference.</para>
///
/// <para>Brio saves only that the actor wore an MCDF and does not restore
/// it. Ktisis saves the path and imports it again, warning when the file has
/// moved. Saving by reference works like Ktisis, with a content hash added;
/// neither Brio nor Ktisis has a portable save.</para>
/// </summary>
public class SceneActorMcdf
{
    /// <summary>The MCDF's full path when saved. Required by reference; empty
    /// when portable, since a path on the saving computer means nothing
    /// elsewhere.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>True when this is an MCDF that was loaded onto the actor. False when it
    /// is the actor's scene appearance, saved by Poser with the scene.
    /// See <c>SceneAppearanceRule</c> for how a load uses it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LoadedOnto { get; set; }

    /// <summary>The file's name, kept next to the path so a load can name the
    /// file in a message even when the path no longer exists.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the MCDF when saved, in uppercase hex. Empty when
    /// the file could not be read while saving; a load still follows such a
    /// reference but cannot confirm it is the same file. When the hash no
    /// longer matches, the load warns rather than quietly importing a
    /// different file. When portable, it is the hash of the included MCDF and
    /// is required: an included MCDF whose hash does not match is not
    /// imported.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// The name of the entry inside the scene file that holds the MCDF. Only
    /// present when portable.
    ///
    /// <para>The MCDF is a separate entry, not part of the JSON. A real one
    /// can be hundreds of megabytes; inside the JSON it would grow by a third
    /// and have to sit in the game's memory all at once on every read and
    /// write. As a separate entry it is written and read in pieces, so a large
    /// MCDF costs only disk space.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageEntry { get; set; }

    /// <summary>The size of the included MCDF in bytes, recorded when saving.
    /// It lets the library show a scene's real size, and the save preview
    /// show what a save will cost, without opening the file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long PackageBytes { get; set; }

    /// <summary>
    /// Where the MCDF is read from while a save runs. It is never written to
    /// the file: the save sets it, and the writer copies the MCDF from there
    /// into the scene file.
    /// </summary>
    [JsonIgnore]
    public string? PackageSourcePath { get; set; }

    /// <summary>Whether this entry carries the package itself.</summary>
    [JsonIgnore]
    public bool IsPortable => PackageEntry is { Length: > 0 };
}

/// <summary>
/// One actor's gaze settings. An actor target is saved as that actor's key
/// in this file, not as its id in the game: every actor in a loaded scene is
/// newly spawned, so a saved game id would point at nothing. A gaze that
/// followed an actor the save did not include saves no target.
/// </summary>
[Serializable]
public class SceneActorGaze
{
    public bool PoseAware { get; set; }
    public GazeTargetMode Mode { get; set; } = GazeTargetMode.None;

    /// <summary>Which parts follow the target.</summary>
    public GazeTargetType Parts { get; set; } = GazeTargetType.All;

    /// <summary>The followed actor's in-document key; null when the gaze
    /// follows no actor.</summary>
    public Guid? TargetActorKey { get; set; }

    /// <summary>The point all parts share in Position mode.</summary>
    public Vector3 Position { get; set; }

    public Vector3 EyesPosition { get; set; }
    public Vector3 HeadPosition { get; set; }
    public Vector3 BodyPosition { get; set; }

    /// <summary>The parts frozen at their own target.</summary>
    public GazeTargetType LockedParts { get; set; } = GazeTargetType.None;
}

/// <summary>One spawned prop: the weapon model ids needed to spawn it again,
/// and where it is in the world.</summary>
[Serializable]
public class SceneProp
{
    public Guid Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public ushort Model { get; set; }
    public ushort Submodel { get; set; }
    public byte Variant { get; set; }

    /// <summary>The two dyes and the pose variant, which the game sets when
    /// it creates the model. Absent reads as undyed.</summary>
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
    public byte AnimationVariant { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>The colour the whole model is multiplied by. Absent is white, which
    /// leaves the model unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Numerics.Vector4? Tint { get; set; }

    public LightFile.TransformData Transform { get; set; } =
        LightFile.TransformData.Identity;
}

/// <summary>
/// One overlay: its key in this file and the overlay's whole state. The
/// editor and undo already keep an overlay's state as one value, so the scene
/// stores that value as it is, just as a scene light stores a whole
/// <see cref="LightFile"/>.
/// </summary>
[Serializable]
public class SceneOverlay
{
    public Guid Key { get; set; }

    /// <summary>The overlay's whole state. Required: an entry without it is
    /// not valid.</summary>
    public OverlayNodeState? Node { get; set; }

    /// <summary>True when the overlay's position is measured from the centre
    /// of the screen rather than in pixels from the corner, so it stays in
    /// place at another resolution or screen shape. Older files read as
    /// pixels from the corner.</summary>
    public bool CenterRelative { get; set; }
}

/// <summary>
/// One world object in the scene, either taken over from the map or spawned
/// by Poser: which object it is, and what was done to it.
///
/// <para>A map object is identified by <see cref="Path"/> and
/// <see cref="MapPosition"/>: the model it draws and where the map places it.
/// Its memory address cannot be used, because that changes every time the
/// game runs. The map position is read from where the map placed the object,
/// so it is the same on every visit to the zone and does not change when the
/// object is dragged.</para>
/// </summary>
[Serializable]
public class SceneWorldObject
{
    public Guid Key { get; set; }

    /// <summary>The model's path. Half of what identifies the object; also its
    /// name in the list.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Where the map places this object. The other half of what
    /// identifies it. It is matched with a small tolerance, because a number
    /// saved to a file and read back is not always exactly the same.
    /// </summary>
    public Vector3 MapPosition { get; set; }

    /// <summary>Where the object was left. Absolute, like every other position
    /// in this file.</summary>
    public LightFile.TransformData Transform { get; set; } =
        LightFile.TransformData.Identity;

    public bool Visible { get; set; } = true;

    /// <summary>Whether Poser created this object rather than taking it over
    /// from the map. A spawned object is restored by spawning its model again,
    /// in any zone; a taken-over one is found on the map again. Older files
    /// only have taken-over objects.</summary>
    public bool Spawned { get; set; }

    /// <summary>The name given to it; empty means the name comes from the
    /// path.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How opaque it is drawn; 1 is fully drawn. Older files read
    /// as 1.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>The colour the object is tinted with; null leaves its own
    /// colours alone.</summary>
    public Vector3? Tint { get; set; }

    /// <summary>Whether a spawned effect replays on its interval.</summary>
    public bool VfxLoop { get; set; } = true;

    /// <summary>Furniture stain; zero uses the furnishing's default. Tint, when present, takes precedence.</summary>
    public byte Stain { get; set; }
    public FurnitureLightState[] FurnitureLights { get; set; } = [];

    /// <summary>A spawned effect's playback speed.</summary>
    public float VfxSpeed { get; set; } = 1f;

    /// <summary>A spawned effect's brightness; 1 is as the effect was made.
    /// </summary>
    public float VfxIntensity { get; set; } = 1f;

    /// <summary>Whether a spawned effect is frozen mid-frame.</summary>
    public bool VfxPaused { get; set; }

    /// <summary>Whether the model shows its night look. Absent reads as day
    /// (off).</summary>
    public bool NightState { get; set; }

    /// <summary>Whether an animated model's motion is frozen.</summary>
    public bool AnimPaused { get; set; }
}

/// <summary>One item in the sidebar: an entity of the named kind (actor,
/// prop, worldObject, light, camera, overlay) by the key its list uses, or a
/// group by its key (Kind "group"). Kind is text, so a kind added later is
/// skipped by an older Poser instead of failing the whole file.</summary>
[Serializable]
public class SceneStructureRef
{
    public string Kind { get; set; } = string.Empty;
    public Guid Key { get; set; }
}

/// <summary>One named group in the sidebar: its name, its members, and its
/// saved transform if it has one.</summary>
[Serializable]
public class SceneGroupEntry
{
    public Guid Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<SceneStructureRef> Members { get; set; } = new();

    /// <summary>The group this one nests in, by key; null at the root.</summary>
    public Guid? Parent { get; set; }

    /// <summary>The group's saved transform. Older files do not have it; the
    /// load then sets one up once the members are placed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneGroupTransformEntry? Transform { get; set; }

    /// <summary>Only in files from an early version of groups, which saved
    /// just the group's rotation. New saves use <see cref="Transform"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Quaternion? InitialFrameRotation { get; set; }
}

[Serializable]
public class SceneGroupTransformEntry
{
    public Vector3 FrameOrigin { get; set; }
    public Quaternion FrameRotation { get; set; } = Quaternion.Identity;
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 SpacingScale { get; set; } = Vector3.One;
    public Vector3 OwnScale { get; set; } = Vector3.One;
    public List<SceneGroupTransformMember> Members { get; set; } = new();
}

[Serializable]
public class SceneGroupTransformMember
{
    public SceneStructureRef Member { get; set; } = new();
    public PoseTransform Initial { get; set; }
    public PoseTransform Expected { get; set; }
}

[Serializable]
public class SceneBoneAttachment
{
    public Guid ActorKey { get; set; }
    public PoseSlot Slot { get; set; } = PoseSlot.Character;
    public int PartialId { get; set; }
    public string BoneName { get; set; } = string.Empty;
}

/// <summary>One scene light: the whole light document and, if it has one,
/// the bone it is attached to.</summary>
[Serializable]
public class SceneLight
{
    public Guid Key { get; set; }
    public LightFile? Light { get; set; }
    public SceneBoneAttachment? Attachment { get; set; }
}

/// <summary>One camera: the whole camera document plus what a .xivc file
/// leaves out: whether it is live, whether it is the default camera, and the
/// actor it follows.</summary>
[Serializable]
public class SceneCamera
{
    public Guid Key { get; set; }
    public CameraFile? Camera { get; set; }
    public bool IsLive { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>The followed actor's in-document key; null when the camera
    /// follows nothing.</summary>
    public Guid? TargetActorKey { get; set; }
    public string TargetActorName { get; set; } = string.Empty;
    public Vector3 TargetOffset { get; set; }
    /// <summary>Whether the camera keeps following the same actor until
    /// unlocked.</summary>
    public bool IsTargetLocked { get; set; }
}

/// <summary>
/// Two switches for the whole scene rather than for any one thing in it:
/// freeze water and freeze physics. They are kept apart from
/// <see cref="SceneEnvironment"/>, which holds values, because these two are
/// simply on or off.
///
/// <para>Brio saves frozen water too. Neither Brio nor Ktisis saves frozen
/// physics.</para>
/// </summary>
[Serializable]
public class SceneWorld
{
    /// <summary>Water stops updating, so every water surface stands still.</summary>
    public bool IsWaterFrozen { get; set; }

    /// <summary>Physics is frozen for the whole scene, not per actor.</summary>
    public bool IsPhysicsFrozen { get; set; }

    /// <summary>Both are off, so the scene saves no World entry.
    /// </summary>
    public bool IsDefault => !IsWaterFrozen && !IsPhysicsFrozen;
}

/// <summary>Time, weather and the environment sections Poser holds. A
/// section's values are saved only when it is held; a section that is not
/// held follows the game and saves nothing.</summary>
[Serializable]
public class SceneEnvironment
{
    public int MinuteOfDay { get; set; }
    public int DayOfMonth { get; set; } = 1;
    public bool IsTimeFrozen { get; set; }
    public uint WeatherId { get; set; }

    /// <summary>The weather's name when saved, only for showing; a load uses
    /// the id. It lets the library say "Rain" without the game's weather
    /// list.</summary>
    public string WeatherName { get; set; } = string.Empty;

    public bool IsWeatherOverrideEnabled { get; set; }
    public float TransitionTime { get; set; } = 0.5f;

    public List<EnvSection> HeldSections { get; set; } = new();

    public EnvSkyValues? Sky { get; set; }
    public EnvCloudsValues? Clouds { get; set; }
    public EnvLightingValues? Lighting { get; set; }
    public EnvFogValues? Fog { get; set; }
    public EnvRainValues? Rain { get; set; }
    public EnvParticlesValues? Particles { get; set; }
    public EnvStarsValues? Stars { get; set; }
    public EnvWindValues? Wind { get; set; }
}
