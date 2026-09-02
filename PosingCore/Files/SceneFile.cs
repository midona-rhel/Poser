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
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Poser scene file format (.xivs): one versioned JSON
/// document carrying every entity of a scene — actors with their complete
/// embedded Brio-format <see cref="PoseFile"/>, props, lights and cameras as
/// their existing per-entity documents (<see cref="LightFile"/>,
/// <see cref="CameraFile"/>), the environment, and the explicit relationships
/// between them (companion attachment, light bone attachment, camera target).
///
/// Entity payloads deliberately embed the EXISTING codecs rather than
/// restating their fields: there is one codec per entity kind, and a light
/// that round-trips through a scene is bit-for-bit the light that round-trips
/// through a .xivl — including that codec's own FileVersion semantics.
///
/// <see cref="SceneId"/> is the document's stable logical identity. It
/// persists across saves of the same scene and is the exact identity a scene
/// operation's <c>OperationReceipt</c> targets, since a whole-scene operation
/// has no single target actor.
/// </summary>
[Serializable]
public class SceneFile
{
    /// <summary>Bumped on any breaking meaning change of a persisted field.
    /// Readers refuse versions above this as typed Future outcomes instead
    /// of guessing at unknown semantics.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The one extension every scene reader, writer and listing
    /// filters on.</summary>
    public const string Extension = ".xivs";

    /// <summary>
    /// An actor library entry: the SAME container and document as a scene,
    /// restricted to exactly one actor and nothing else. It gets its own
    /// extension so the library can tab it without opening it; every codec,
    /// store and restore path treats it as the scene it is.
    /// </summary>
    public const string ActorEntryExtension = ".xiva";

    /// <summary>Placement anchors: where the camera and the anchor actor
    /// stood at capture, yaw-flattened. Stamped on every capture (they cost
    /// nothing); an actor ENTRY load is what reads them.</summary>
    public PlacementAnchorData? CameraAnchor { get; set; }
    public PlacementAnchorData? ActorAnchor { get; set; }

    /// <summary>
    /// An environment library entry: the scene container restricted to the
    /// environment configuration — weather, sky, atmosphere, world rendering
    /// — and nothing else. Same codec, own extension so the library tabs it
    /// without opening it.
    /// </summary>
    public const string EnvironmentEntryExtension = ".xive";

    /// <summary>
    /// An overlay library entry: the scene container restricted to one
    /// overlay node.
    /// </summary>
    public const string OverlayEntryExtension = ".xivo";

    /// <summary>
    /// A group library entry: the scene container restricted to one named
    /// group's members, the group itself riding along so a load recreates
    /// it whole. Same codec, own extension so the library tabs it without
    /// opening it.
    /// </summary>
    public const string GroupEntryExtension = ".xivg";

    /// <summary>
    /// A world-object library entry: the scene container restricted to one
    /// world object saved as a SPAWNABLE copy — the entry carries the model
    /// path marked spawned, so activation creates it anywhere, any zone.
    /// </summary>
    public const string WorldObjectEntryExtension = ".xivw";

    /// <summary>A prop entry: one spawned weapon-model prop — its model
    /// triple, dyes, and pose variant — as a scene container.</summary>
    public const string PropEntryExtension = ".xivp";

    /// <summary>
    /// A light library entry. ONE pipeline for every entry (ruled
    /// 2026-08-31): the scene container restricted to one light, saved
    /// through the workflow and restored through the load — the pane-direct
    /// LightFile write this replaced left old-format entries behind, which
    /// read as unreadable and are re-saved.
    /// </summary>
    public const string LightEntryExtension = ".xivl";

    /// <summary>The camera's twin of <see cref="LightEntryExtension"/>.
    /// </summary>
    public const string CameraEntryExtension = ".xivc";

    public string TypeName { get; set; } = "XIV Scene";
    public int FileVersion { get; set; } = CurrentVersion;

    /// <summary>Stable logical scene identity; never empty in a valid file.</summary>
    public Guid SceneId { get; set; }

    public string? Author { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? SavedAt { get; set; }

    /// <summary>The territory the capture ran in; 0 means unknown, which is
    /// every file written before scenes recorded where they were taken.
    /// </summary>
    public uint TerritoryId { get; set; }

    /// <summary>The territory's place name, resolved AT CAPTURE. The name is
    /// persisted BESIDE the id, not derived from it, because neither the codec
    /// nor the library scan has game data to resolve an id with — a listing
    /// must be able to say where a scene was taken with the game shut. Absent
    /// on files written before scenes recorded it, and on a capture whose
    /// territory had no name.</summary>
    public string? PlaceName { get; set; }

    /// <summary>
    /// Where the capture STOOD: the local player's world position at save. It
    /// is the anchor a relative load rebases onto, and nothing else — every
    /// placement in this document stays ABSOLUTE, which is the invariant a
    /// reader that ignores this field depends on.
    ///
    /// <para>Both references encode the same anchor and neither states it the
    /// same way: Ktisis stores every actor position ALREADY relative to its
    /// <c>SceneOrigin</c> and adds the origin back on load
    /// (<c>Services/Data/SceneDataService.cs</c>), while Brio keeps absolutes
    /// and anchors on the live local player at import
    /// (<c>Services/SceneService.cs</c>, <c>useRelativeLightPositions</c>).
    /// Poser follows Brio's shape — absolutes on the wire — because a document
    /// whose numbers only mean something beside an origin cannot be read by a
    /// listing, a diff, or a reader that never asked for a relative load.</para>
    ///
    /// <para>Absent on a capture with no local player to anchor on and on
    /// every file written before the anchor was recorded; a relative load of
    /// such a file is REFUSED by name rather than rebased onto a guess.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Vector3? Origin { get; set; }

    public List<SceneActor> Actors { get; set; } = new();
    public List<SceneProp> Props { get; set; } = new();
    public List<SceneLight> Lights { get; set; } = new();
    public List<SceneCamera> Cameras { get; set; } = new();
    public SceneEnvironment? Environment { get; set; }

    /// <summary>Session-wide render and simulation toggles. Absent when every
    /// one of them sits at the game's own behaviour, which is what a scene
    /// written before they were recorded says.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneWorld? World { get; set; }

    /// <summary>The staged game-UI overlay nodes. ABSENT rather than empty
    /// when the scene has none, which is every scene written before overlay
    /// nodes existed: an older file reads back byte-identical and a scene with
    /// no nodes writes no list at all.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneOverlay>? Overlays { get; set; }

    /// <summary>The map's own objects the scene had BORROWED. ABSENT rather
    /// than empty when the scene had none, which is every scene written before
    /// world objects could be adopted: an older file reads back byte-identical
    /// and a scene that borrowed nothing writes no list at all.
    ///
    /// <para>These entries are the one part of a scene that only means
    /// something WHERE IT WAS TAKEN. A BG object has no id that survives a
    /// session — its address is this process's — so an entry names it by the
    /// model path plus the point the MAP stands it at, and a load outside
    /// <see cref="TerritoryId"/> refuses every entry BY NAME rather than
    /// re-adopting whatever happens to share a path in another zone.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneWorldObject>? WorldObjects { get; set; }

    /// <summary>The sidebar's named groups. ABSENT rather than empty when
    /// the scene has none, so every older file reads back byte-identical.
    /// Members reference entities by the SAME keys the entity lists above
    /// carry; a member the load cannot restore is skipped by name, and a
    /// group thinned below two members dissolves exactly as it does
    /// live.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneGroupEntry>? Groups { get; set; }

    /// <summary>The sidebar's root order — the USER'S arrangement, kinds
    /// interleaved, group heads included (Kind "group", keyed by the
    /// group entry's key). ABSENT when unrecorded; a load without it
    /// seats entities in kind order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SceneStructureRef>? RootOrder { get; set; }

    // The same wire style every Poser document uses — numerics as
    // comma-space strings, enums by name, PascalCase, pretty printing,
    // relaxed escaping, tolerated trailing commas and unknown members.
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

/// <summary>Hard bounds every scene read and write enforces.</summary>
public static class SceneFileLimits
{
    /// <summary>
    /// The DOCUMENT's cap — the JSON entry inside the container, which holds
    /// one complete pose document per actor and no payload bytes at all. It is
    /// double the ordinary pose cap for the same reason it always was.
    ///
    /// <para>This is deliberately NOT a cap on the file: appearance payloads
    /// are separate container entries, so a scene carrying half a gigabyte of
    /// packages still has a small document.</para>
    /// </summary>
    public const long MaxDocumentBytes = 64L * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxActors = 100;
    public const int MaxProps = 100;

    /// <summary>A scene borrows map objects one click at a time; the cap is the
    /// props' own, for the same reason.</summary>
    public const int MaxWorldObjects = 100;
    public const int MaxLights = 50;
    public const int MaxCameras = 50;
    public const int MaxOverlays = 50;
    public const int MaxNameCharacters = 256;

    /// <summary>Bound for stated filesystem paths, which are legitimately
    /// longer than a name — a long-path prefix plus a deep library.</summary>
    public const int MaxPathCharacters = 1024;

    /// <summary>Hex characters of a SHA-256 digest.</summary>
    public const int ContentHashCharacters = 64;

    /// <summary>
    /// One actor's embedded appearance package. It matches the MCDF importer's
    /// own ceiling (<c>IntegrationConfiguration.McdfMaxFileBytes</c>) because a
    /// package Poser will happily IMPORT must be a package it can SAVE — real
    /// character files run to hundreds of megabytes, and a scene format that
    /// refuses them is a scene format that cannot record appearance.
    ///
    /// <para>Payloads are stored as their own container entries and streamed,
    /// never encoded into the document, so this bounds disk rather than
    /// memory.</para>
    /// </summary>
    public const long MaxEmbeddedAppearanceBytes = 512L * 1024 * 1024;

    /// <summary>Above this, a save still writes the payload and SAYS how big
    /// the file became. It is a warning threshold, never a refusal: a user who
    /// asked for portable appearance gets portable appearance.</summary>
    public const long LargeAppearanceWarningBytes = 256L * 1024 * 1024;
    public const float MinQuaternionLengthSquared =
        PoseFileLimits.MinQuaternionLengthSquared;
}

/// <summary>
/// One saved actor: its stable in-document key, respawn facts, companion
/// attachment, where it stands, what it is playing, where it is looking, and
/// the complete embedded pose document. Appearance beyond the Model ID stays
/// with its external owners (Glamourer/MCDF) and is deliberately not scene
/// data.
/// </summary>
[Serializable]
public class SceneActor
{
    /// <summary>Stable in-document identity; relationship fields refer to it.
    /// Independent of any native binding generation.</summary>
    public Guid Key { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The actor's ModelChara row id; 0 is the human base.</summary>
    public int ModelCharaId { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>Whether the actor reserved a companion slot at spawn. Required
    /// for a companion attachment to be restorable.</summary>
    public bool HasCompanionSlot { get; set; }

    /// <summary>Attached companion/mount/ornament; ABSENT when the slot is
    /// empty. Nothing attached is the absence of an attachment, never a
    /// kind — no sheet describes an empty slot — so an empty slot writes no
    /// kind rather than a named one.</summary>
    public CompanionKind? CompanionKind { get; set; }

    /// <summary>The attachment's row id; 0 when <see cref="CompanionKind"/>
    /// is absent.</summary>
    public ushort CompanionId { get; set; }

    /// <summary>
    /// The attached companion's OWN pose document, as a complete pose in its
    /// own right — a minion, mount or ornament has a skeleton and can be posed
    /// like any other body, and restoring only the ATTACHMENT brings it back
    /// idling in whatever the game hands it. Brio saves the same thing
    /// (<c>ChildActor.PoseFile</c>, ActorDTO.cs:137); Ktisis has no companion
    /// concept in its scene at all.
    ///
    /// <para>Absent when the slot is empty, when the companion had no skeleton
    /// to read, or when the companion was never posed away from its idle — so a
    /// scene written before companion poses existed reads back byte-identical.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PoseFile? CompanionPose { get; set; }

    /// <summary>The complete Brio-format pose document, validated by the
    /// ordinary pose codec rules. Required — a scene actor without a pose is
    /// not a saved scene.</summary>
    public PoseFile? Pose { get; set; }

    /// <summary>
    /// Where the actor STANDS: the absolute world transform of its draw
    /// object, stated by the scene layer in its own right.
    ///
    /// <para>The embedded pose carries a model transform too, but only as the
    /// pose codec's <c>ModelAbsoluteValues</c>, whose "nothing was recorded"
    /// marker is <c>BoneData.Identity</c> — zero position, identity rotation,
    /// ZERO scale. An actor genuinely standing at the world origin unrotated
    /// is therefore indistinguishable from an unrecorded one, and the restore
    /// silently placed nothing. ABSENT here is the only statement of "this
    /// file records no placement", so present always means place it.</para>
    ///
    /// <para>Omitted when unset, so a scene written before placements were
    /// stated reads back byte-identical and falls back to the embedded pose's
    /// absolute values exactly as it always did.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LightFile.TransformData? ModelTransform { get; set; }

    /// <summary>Where the actor is LOOKING. Absent when no gaze override is
    /// configured, which is the ordinary case.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneActorGaze? Gaze { get; set; }

    /// <summary>The character file the actor is WEARING. Absent when the
    /// actor's appearance is not an imported MCDF, which is the ordinary
    /// case.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneActorMcdf? Mcdf { get; set; }
}

/// <summary>
/// The character file an actor is wearing, in ONE of two modes — and the mode
/// is the difference between a scene that travels and one that does not.
///
/// <para>REFERENCE mode (<see cref="Package"/> absent) states where the package
/// was and lets the existing import machinery read it again. It is the default
/// because an MCDF is tens of megabytes of another player's mods, and it is
/// only meaningful on the machine that saved it.</para>
///
/// <para>PORTABLE mode (<see cref="Package"/> present) carries the package's
/// own bytes. A path, a temporary collection id, or any other live handle is
/// NOT a portable save — it names something the receiving machine does not
/// have — so a save the user asked to make portable either embeds the bytes or
/// refuses by name and saves the actor without appearance. It never keeps the
/// reference and calls itself portable.</para>
///
/// <para>Divergence from both references, deliberately: Brio records only a
/// <c>WasMCDF</c> boolean and then explicitly REFUSES to restore the appearance
/// ("was locked at the time of saving. Appearance will not be imported",
/// SceneService.cs:516-519). Ktisis records the path
/// (<c>SceneFile.ActorInfo.MCDF</c>) and re-imports it, warning by name when
/// the file has moved (SceneDataService.cs:429-437) — reference mode follows
/// Ktisis and adds the content hash Ktisis has no equivalent of; portable mode
/// is Poser's own and neither reference has anything like it.</para>
/// </summary>
[Serializable]
public class SceneActorMcdf
{
    /// <summary>The package's full path AT SAVE. Required in reference mode;
    /// EMPTY in portable mode, where the bytes are the document's own and no
    /// path on the saving machine means anything to a reader.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The display name, kept beside the path so a load can name the
    /// file in a refusal without parsing a path that may no longer exist.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the package's bytes at save, uppercase hex. EMPTY
    /// when the file could not be read while saving — an unverifiable
    /// reference, which a load still follows but cannot vouch for. A hash that
    /// no longer matches is a named warning on load, never a silent import of
    /// different content. In portable mode it is the digest of
    /// <see cref="Package"/> and is REQUIRED: embedded bytes whose hash does
    /// not check out are refused rather than imported.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// The container entry holding this package's bytes, present only in
    /// portable mode.
    ///
    /// <para>The bytes are an entry of their own, NOT a field of this
    /// document. A real character file is hundreds of megabytes; base64 inside
    /// the JSON would inflate it by a third, force the whole thing through a
    /// single string and a single byte[] on every read, every write and every
    /// commit verification, and put a multi-gigabyte transient in a game
    /// process. The entry is written and read as a STREAM, so the cost of a
    /// large payload is disk and nothing else.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageEntry { get; set; }

    /// <summary>The payload entry's length in bytes, as recorded at save. It
    /// lets a listing state a scene's real size, and the save preview state
    /// what a scene is about to cost, without opening the container.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long PackageBytes { get; set; }

    /// <summary>
    /// Where the package is being read FROM while a save is in flight. Never
    /// serialized and never present on a document that came off disk: the
    /// sealing step records it, the writer streams it into the container
    /// entry, and nothing else may look at it.
    /// </summary>
    [JsonIgnore]
    public string? PackageSourcePath { get; set; }

    /// <summary>Whether this entry carries the package itself.</summary>
    [JsonIgnore]
    public bool IsPortable => PackageEntry is { Length: > 0 };
}

/// <summary>
/// One actor's gaze configuration. The Entity target is stated as the
/// in-document ACTOR KEY, never the native GameObjectId it is keyed by at
/// runtime: every actor in a restored scene is freshly spawned, so a saved
/// object id names nothing. A gaze that followed an actor the capture did not
/// take records no target at all.
/// </summary>
[Serializable]
public class SceneActorGaze
{
    public GazeTargetMode Mode { get; set; } = GazeTargetMode.None;

    /// <summary>Which parts participate.</summary>
    public GazeTargetType Parts { get; set; } = GazeTargetType.All;

    /// <summary>The followed actor's in-document key; null when the gaze
    /// follows no actor.</summary>
    public Guid? TargetActorKey { get; set; }

    /// <summary>The shared Position-mode anchor.</summary>
    public Vector3 Position { get; set; }

    public Vector3 EyesPosition { get; set; }
    public Vector3 HeadPosition { get; set; }
    public Vector3 BodyPosition { get; set; }

    /// <summary>The parts frozen at their own target.</summary>
    public GazeTargetType LockedParts { get; set; } = GazeTargetType.None;
}

/// <summary>One spawned prop: the weapon-model triple that respawns it and
/// its absolute world transform.</summary>
[Serializable]
public class SceneProp
{
    public Guid Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public ushort Model { get; set; }
    public ushort Submodel { get; set; }
    public byte Variant { get; set; }

    /// <summary>The two dye channels and the pose variant, exactly the
    /// facts the native create bakes in. Absent reads undyed.</summary>
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
    public byte AnimationVariant { get; set; }

    public bool Visible { get; set; } = true;
    public LightFile.TransformData Transform { get; set; } =
        LightFile.TransformData.Identity;
}

/// <summary>
/// One staged overlay node: its stable in-document key plus the COMPLETE node
/// document, embedded rather than restated. A node's state is already one
/// value the editor, the undo journal and the native port all speak, so the
/// scene carries that value and nothing else — the same rule that has a scene
/// light carry a whole <see cref="LightFile"/>.
/// </summary>
[Serializable]
public class SceneOverlay
{
    public Guid Key { get; set; }

    /// <summary>The node's whole state. Required — an overlay entry without
    /// one names nothing.</summary>
    public OverlayNodeState? Node { get; set; }

    /// <summary>True when the node's stored position is an offset from the
    /// SCREEN CENTRE rather than absolute pixels — how an overlay survives a
    /// different resolution or aspect ratio. The restore re-attaches it at
    /// the current centre; a file from before the convention reads as the
    /// absolute pixels it was.</summary>
    public bool CenterRelative { get; set; }
}

/// <summary>
/// One BORROWED map object: which object it is, and what the user did to it.
///
/// <para>Identity is the pair <see cref="Path"/> and <see cref="MapPosition"/>
/// — the model file the object draws, and the point the MAP stands it at. It is
/// deliberately not the address the claim was taken at: that address belongs to
/// one run of one process. The map position is captured from the object's own
/// pre-adoption placement, so it is the same value on every visit to the same
/// territory and it does not move when the user drags the object.</para>
/// </summary>
[Serializable]
public class SceneWorldObject
{
    public Guid Key { get; set; }

    /// <summary>The model resource path. Half of the identity; also the row's
    /// name.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Where the MAP stands this object, before any adoption. The
    /// other half of the identity, matched within a small tolerance because a
    /// float that has been through a codec is not the float that went in.
    /// </summary>
    public Vector3 MapPosition { get; set; }

    /// <summary>Where the USER left it. Absolute, like every other placement in
    /// this document.</summary>
    public LightFile.TransformData Transform { get; set; } =
        LightFile.TransformData.Identity;

    public bool Visible { get; set; } = true;

    /// <summary>Whether POSER created this object rather than borrowing it
    /// from the map. A spawned entry restores by SPAWNING its path — any
    /// zone, no map identity to match — where a borrowed one re-adopts the
    /// object the map is standing. Absent on older files, which only ever
    /// borrowed.</summary>
    public bool Spawned { get; set; }

    /// <summary>The user's name for it, when one was given; empty derives
    /// from the path as ever.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The drawn opacity, 1 fully drawn. Absent reads as 1 on
    /// older files.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>The effect's colour multiplier when the user tinted it;
    /// null leaves the file's own colours alone (VFX only).</summary>
    public Vector3? Tint { get; set; }

    /// <summary>Whether a spawned effect replays on its interval.</summary>
    public bool VfxLoop { get; set; } = true;

    /// <summary>A spawned effect's playback speed.</summary>
    public float VfxSpeed { get; set; } = 1f;

    /// <summary>A spawned effect's uniform brightness, 1 as authored.
    /// </summary>
    public float VfxIntensity { get; set; } = 1f;

    /// <summary>Whether a spawned effect is frozen mid-frame.</summary>
    public bool VfxPaused { get; set; }

    /// <summary>The model's day/night dressing. Absent reads DAY (off)
    /// — the ruled default for anything undefined.</summary>
    public bool NightState { get; set; }

    /// <summary>Whether an animated model's motion is frozen.</summary>
    public bool AnimPaused { get; set; }
}

/// <summary>Exact bone identity inside a saved scene: the owning actor's
/// in-document key plus the slot/partial/name triple that resolves the bone
/// on the restored actor. Never a native index or pointer.</summary>
/// <summary>One reference into the scene's structure: an entity of the
/// named kind (actor, prop, worldObject, light, camera, overlay) by the
/// key its entity list carries, or a group by its entry's key (Kind
/// "group"). Kind is a string so an unknown future kind reads and skips
/// rather than failing the file.</summary>
[Serializable]
public class SceneStructureRef
{
    public string Kind { get; set; } = string.Empty;
    public Guid Key { get; set; }
}

/// <summary>One named sidebar group: naming and structure only — a group
/// owns no transform, here exactly as it owns none live.</summary>
[Serializable]
public class SceneGroupEntry
{
    public Guid Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<SceneStructureRef> Members { get; set; } = new();

    /// <summary>The group this one nests in, by key; null at the root.</summary>
    public Guid? Parent { get; set; }
}

[Serializable]
public class SceneBoneAttachment
{
    public Guid ActorKey { get; set; }
    public PoseSlot Slot { get; set; } = PoseSlot.Character;
    public int PartialId { get; set; }
    public string BoneName { get; set; } = string.Empty;
}

/// <summary>One scene light: the complete existing light document plus the
/// optional explicit bone attachment.</summary>
[Serializable]
public class SceneLight
{
    public Guid Key { get; set; }
    public LightFile? Light { get; set; }
    public SceneBoneAttachment? Attachment { get; set; }
}

/// <summary>One virtual camera: the complete existing camera document plus
/// the session facts a .xivc deliberately omits — liveness, whether it is
/// the session default, and the explicit target-actor relationship.</summary>
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
    /// <summary>Whether the target identity stays fixed until unlocked.</summary>
    public bool IsTargetLocked { get; set; }
}

/// <summary>
/// Toggles that belong to the whole session rather than to any one entity: a
/// suppressed water renderer and a suppressed physics simulation. They are not
/// environment values — the environment is a set of held per-section VALUES,
/// while these two are patches whose enabled state is the whole of their
/// state — so they are stated in their own right rather than smuggled into
/// <see cref="SceneEnvironment"/>.
///
/// <para>The water freeze is Brio's <c>SceneFile.IsWaterFrozen</c> (Brio
/// EnvironmentDTO); neither reference records a physics freeze.</para>
/// </summary>
[Serializable]
public class SceneWorld
{
    /// <summary>The water renderer's update suppressed, freezing every
    /// surface.</summary>
    public bool IsWaterFrozen { get; set; }

    /// <summary>The physics simulation suppressed. Global, not per-actor: a
    /// scene states whether IT asked for the freeze, which is exactly what the
    /// shell's own switch owns.</summary>
    public bool IsPhysicsFrozen { get; set; }

    /// <summary>Nothing to state: a scene records no world block at all.
    /// </summary>
    public bool IsDefault => !IsWaterFrozen && !IsPhysicsFrozen;
}

/// <summary>Time, weather and the held environment sections. A section's
/// values are present exactly when that section is held — an unheld section
/// belongs to the game and carries nothing.</summary>
[Serializable]
public class SceneEnvironment
{
    public int MinuteOfDay { get; set; }
    public int DayOfMonth { get; set; } = 1;
    public bool IsTimeFrozen { get; set; }
    public uint WeatherId { get; set; }

    /// <summary>The weather's display name at capture. Display-only — the
    /// restore keys on the id — so a listing can say "Rain" without the
    /// game's weather sheet in hand.</summary>
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
