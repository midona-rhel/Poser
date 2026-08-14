using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Poser scene file format (.poserscene): one versioned JSON
/// document carrying every entity of a scene — actors with their complete
/// embedded Brio-format <see cref="PoseFile"/>, props, lights and cameras as
/// their existing per-entity documents (<see cref="LightFile"/>,
/// <see cref="CameraFile"/>), the environment, and the explicit relationships
/// between them (companion attachment, light bone attachment, camera target).
///
/// Entity payloads deliberately embed the EXISTING codecs rather than
/// restating their fields: there is one codec per entity kind, and a light
/// that round-trips through a scene is bit-for-bit the light that round-trips
/// through a .poserlight — including that codec's own FileVersion semantics.
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
    public const int CurrentVersion = 1;

    /// <summary>The one extension every scene reader, writer and listing
    /// filters on.</summary>
    public const string Extension = ".poserscene";

    public string TypeName { get; set; } = "Poser Scene";
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
    /// <summary>Scenes embed one complete pose document per actor, so the
    /// byte cap is double the ordinary pose cap.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxActors = 100;
    public const int MaxProps = 100;
    public const int MaxLights = 50;
    public const int MaxCameras = 50;
    public const int MaxNameCharacters = 256;

    /// <summary>Bound for stated filesystem paths, which are legitimately
    /// longer than a name — a long-path prefix plus a deep library.</summary>
    public const int MaxPathCharacters = 1024;

    /// <summary>Hex characters of a SHA-256 digest.</summary>
    public const int ContentHashCharacters = 64;
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

    /// <summary>What the actor is PLAYING. Absent when nothing about the
    /// actor's animation was worth recording — no Poser-owned override and a
    /// plain idle at ordinary speed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SceneActorAnimation? Animation { get; set; }

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
/// A REFERENCE to the character file an actor is wearing — never the payload.
/// An MCDF is tens of megabytes of another player's mods; a scene states where
/// it was and lets the existing import machinery read it again.
///
/// <para>Divergence from both references, deliberately: Brio records only a
/// <c>WasMCDF</c> boolean and then explicitly REFUSES to restore the appearance
/// ("was locked at the time of saving. Appearance will not be imported",
/// SceneService.cs:516-519). Ktisis records the path
/// (<c>SceneFile.ActorInfo.MCDF</c>) and re-imports it, warning by name when
/// the file has moved (SceneDataService.cs:429-437) — this follows Ktisis, and
/// adds the content hash Ktisis has no equivalent of.</para>
/// </summary>
[Serializable]
public class SceneActorMcdf
{
    /// <summary>The package's full path AT SAVE. Required.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The display name, kept beside the path so a load can name the
    /// file in a refusal without parsing a path that may no longer exist.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the package's bytes at save, uppercase hex. EMPTY
    /// when the file could not be read while saving — an unverifiable
    /// reference, which a load still follows but cannot vouch for. A hash that
    /// no longer matches is a named warning on load, never a silent import of
    /// different content.</summary>
    public string ContentHash { get; set; } = string.Empty;
}

/// <summary>
/// One actor's animation state. Every member here has an APPLY route in
/// <c>AnimationSession</c> — a scene never records animation facts it cannot
/// put back. Base timeline, speed, lips, stance/pose and weapon are the LIVE
/// reading (what the actor is doing); the held expression, the per-slot speeds
/// and the armed loops are Poser-owned overrides, which have no live field to
/// read and exist only in the session.
/// </summary>
[Serializable]
public class SceneActorAnimation
{
    /// <summary>The base slot's timeline; 0 means the actor was on whatever
    /// the game gives it and nothing is replayed.</summary>
    public ushort BaseTimeline { get; set; }

    /// <summary>Overall playback speed. 0 IS the pause state — a paused actor
    /// is one whose speed override is zero, which is the only pause either
    /// reference has.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Speech timeline override; 0 means none.</summary>
    public ushort Lips { get; set; }

    public bool WeaponDrawn { get; set; }
    public AnimationStance Stance { get; set; } = AnimationStance.Idle;
    public int Pose { get; set; }

    /// <summary>The expression pinned onto the facial layer; 0 means none.
    /// Restored through the same hold mechanism that authored it, so the
    /// facial pin comes back with it rather than as a bare slot speed.
    /// </summary>
    public ushort HeldExpression { get; set; }

    public bool PositionLock { get; set; }

    /// <summary>Per-slot overrides, one entry per slot Poser owns something
    /// on. A list rather than a keyed map: the wire shape then matches
    /// <see cref="SceneEnvironment.HeldSections"/> and never depends on how a
    /// serializer chooses to spell an enum used as a dictionary key.</summary>
    public List<SceneAnimationSlot> Slots { get; set; } = new();

    /// <summary>
    /// Where a PAUSED timeline actually stands — the exact frame the user
    /// scrubbed to, which the speed and the timeline id together cannot
    /// express. Recorded only while <see cref="Speed"/> is zero: a running
    /// animation's frame is whatever the game advanced it to this tick and
    /// means nothing an instant later, so writing one back would be inventing
    /// a fact. Empty for a running actor, and for every scene written before
    /// frames were recorded.
    /// </summary>
    public List<SceneAnimationFrame> Frames { get; set; } = new();
}

/// <summary>One paused control's local time, named by the SLOT it drives
/// rather than by a control index: an index is a position in a freshly
/// enumerated native list, and a saved one would name whatever occupies that
/// position on a restored skeleton.</summary>
[Serializable]
public class SceneAnimationFrame
{
    public AnimationSlot Slot { get; set; } = AnimationSlot.Base;

    /// <summary>Local time within the control, in seconds.</summary>
    public float Time { get; set; }
}

/// <summary>One animation slot's owned state: its pinned speed, its armed
/// loop, or both.</summary>
[Serializable]
public class SceneAnimationSlot
{
    public AnimationSlot Slot { get; set; } = AnimationSlot.Base;

    /// <summary>The pinned playback speed; absent when Poser owns no speed on
    /// this slot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float? Speed { get; set; }

    /// <summary>The armed loop's timeline; 0 means no loop.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ushort Loop { get; set; }
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
    public bool Visible { get; set; } = true;
    public LightFile.TransformData Transform { get; set; } =
        LightFile.TransformData.Identity;
}

/// <summary>Exact bone identity inside a saved scene: the owning actor's
/// in-document key plus the slot/partial/name triple that resolves the bone
/// on the restored actor. Never a native index or pointer.</summary>
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
/// the session facts a .posercam deliberately omits — liveness, whether it is
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
