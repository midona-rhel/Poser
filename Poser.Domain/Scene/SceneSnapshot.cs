using System.Numerics;
using Poser.Domain.Collections;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Domain.Scene;

public sealed record BoneDescriptor(
    BoneId Id,
    string DisplayName,
    BoneId? Parent,
    bool IsHidden = false);

public sealed record SkeletonDescriptor(
    SkeletonId Id,
    IReadOnlyList<BoneDescriptor> Bones)
{
    /// <summary>Stored as a <see cref="ValueList{T}"/> so record equality
    /// compares bones by content.</summary>
    public IReadOnlyList<BoneDescriptor> Bones { get; init => field = ValueList.From(value); } =
        ValueList.From(Bones);

    public PoseSlot Slot => Id.Slot;
}

/// <summary>
/// Owns the pointer-free facts for one actor and its present slot skeletons.
/// It does not own native handles, transforms, or scene indexing.
/// </summary>
/// <param name="OwnerActor">The exact actor generation this companion is
/// attached to, when that relationship is known. The companion remains its
/// own actor and state owner.</param>
/// <param name="AttachmentKind">The exact owner-slot relationship. Null for
/// root actors and standalone catalog creatures.</param>
public sealed record ActorDescriptor(
    ActorId Id,
    string Name,
    IReadOnlyList<SkeletonDescriptor> Skeletons,
    bool IsPlayer = false,
    bool IsCompanion = false,
    bool IsHidden = false,
    ActorId? OwnerActor = null,
    CompanionKind? AttachmentKind = null,
    bool IsOwned = true,
    bool IsAdopted = false)
{
    /// <summary>Stored as a <see cref="ValueList{T}"/> so record equality
    /// compares skeletons by content.</summary>
    public IReadOnlyList<SkeletonDescriptor> Skeletons { get; init => field = ValueList.From(value); } =
        ValueList.From(Skeletons);

    /// <summary><see cref="IsAdopted"/>: an overworld body taken into the
    /// scene by reference; it is released, never destroyed.</summary>
    /// <summary><see cref="IsOwned"/>: the actor was spawned by Poser or
    /// is the player's own character. Only an owned actor's character
    /// data — appearance, MCDF — may be exported or saved; anyone else's
    /// (a friend posed in GPose) may be posed but never packaged.</summary>
    /// <summary>The Character-slot skeleton; callers needing another slot use
    /// <see cref="GetSkeleton"/>.</summary>
    public SkeletonDescriptor? CharacterSkeleton =>
        GetSkeleton(PoseSlot.Character);

    public SkeletonDescriptor? GetSkeleton(PoseSlot slot)
    {
        foreach (var skeleton in Skeletons)
        {
            if (skeleton.Id.Slot == slot)
                return skeleton;
        }
        return null;
    }
}

public enum LightKind
{
    Directional,
    Point,
    Spot,
    Area,
}

public enum LightOwnership
{
    /// <summary>Plugin-created and eligible for native destruction.</summary>
    Spawned,
    /// <summary>Borrowed from the GPose lighting setup.</summary>
    GPose,
    /// <summary>Captured from the world and restored on release.</summary>
    World,
}

/// <summary>
/// Owns the pointer-free light row state and optional exact bone attachment.
/// It does not own live light properties or native light lifetime.
/// </summary>
public sealed record LightDescriptor(
    LightId Id,
    string Name,
    LightKind Kind,
    bool IsOn = true,
    LightOwnership Ownership = LightOwnership.Spawned,
    BoneId? AttachedBone = null);

public enum CameraKind
{
    /// <summary>Uses the game's orbit camera.</summary>
    Game,
    /// <summary>Owns a free-camera view.</summary>
    Free,
}

/// <summary>
/// Owns the pointer-free camera row state, lock, and stable target relationship.
/// It does not own the live camera or its native view matrix.
/// </summary>
public sealed record CameraDescriptor(
    CameraId Id,
    string Name,
    CameraKind Kind,
    bool IsLive = false,
    bool IsDefault = false,
    bool IsLocked = false,
    ActorId? TargetActor = null,
    BoneId? TargetBone = null,
    Vector3 TargetOffset = default);

/// <summary>
/// Owns the pointer-free prop row state. It does not own the live prop handle
/// or native transform.
/// </summary>
public sealed record PropDescriptor(
    PropId Id,
    string Name,
    bool Visible = true);

/// <summary>
/// Owns the pointer-free ADOPTED-WORLD-OBJECT row state. It does not own the
/// live claim or the native object under it. The PATH rides along because it
/// is the only human-readable thing a BG object carries and the only half of
/// its identity that survives the session the addresses belong to.
/// </summary>
public sealed record WorldObjectDescriptor(
    WorldObjectId Id,
    string Name,
    string Path,
    bool Visible = true,
    bool Spawned = false,
    bool VfxPaused = false,
    bool AnimPaused = false,
    bool Night = false);

/// <summary>
/// Owns the pointer-free overlay-node row state. It does not own the live
/// node or its native UI subtree. The KIND rides along because it decides the
/// row's mark and the editor it opens, and nothing else about a node is a row
/// fact — its text, its colours and its screen placement all live in the
/// editor.
/// </summary>
public sealed record OverlayDescriptor(
    OverlayId Id,
    string Name,
    OverlayNodeKind Kind,
    bool Visible = true);

[Flags]
public enum EnvironmentSection
{
    None = 0,
    Sky = 1 << 0,
    Clouds = 1 << 1,
    Lighting = 1 << 2,
    Fog = 1 << 3,
    Rain = 1 << 4,
    Particles = 1 << 5,
    Stars = 1 << 6,
    Wind = 1 << 7,
    All = Sky | Clouds | Lighting | Fog | Rain | Particles | Stars | Wind,
}

/// <summary>
/// Pointer-free environment read state justified by the existing time,
/// weather, and section-hold controls. It does not own native environment
/// state or write policy.
/// </summary>
public sealed record EnvironmentDescriptor(
    int MinuteOfDay,
    int DayOfMonth,
    uint WeatherId,
    bool IsTimeFrozen = false,
    bool IsWeatherOverrideEnabled = false,
    EnvironmentSection HeldSections = EnvironmentSection.None);

public enum GazeMode
{
    Off,
    Forward,
    Camera,
    Actor,
    Position,
}

[Flags]
public enum GazeParts
{
    None = 0,
    Body = 1,
    Head = 4,
    Eyes = 8,
    All = Body | Head | Eyes,
}

/// <summary>
/// Owns one actor's pointer-free gaze read state and exact actor target. It
/// does not own native look-at entries or per-frame gaze writes.
/// </summary>
public sealed record GazeDescriptor(
    ActorId Actor,
    GazeMode Mode = GazeMode.Off,
    GazeParts Parts = GazeParts.All,
    GazeParts LockedParts = GazeParts.None,
    ActorId? TargetActor = null,
    Vector3 Anchor = default,
    Vector3 EyesPosition = default,
    Vector3 HeadPosition = default,
    Vector3 BodyPosition = default)
{
    /// <summary>The shared Position-mode anchor used by the world gizmo.</summary>
    public Vector3 Position => Anchor;
}

/// <summary>
/// Immutable, pointer-free application read state for one logical scene. It
/// owns no native entities and carries no application indexes. SceneSession is
/// the committed Application owner of this state, while the current Game
/// producer remains a transitional candidate source and may leave additive
/// relationship, environment, and gaze fields empty until lifecycle
/// integration is serialized. Every list member, here and in the nested
/// descriptors, is stored as a <see cref="ValueList{T}"/>, so generated record
/// equality is structural all the way down and covers every field without a
/// hand-written comparison to keep in step.
/// </summary>
public sealed record SceneSnapshot
{
    public SceneSnapshot(
        ulong Revision,
        IReadOnlyList<ActorDescriptor> Actors,
        IReadOnlyList<LightDescriptor> Lights,
        IReadOnlyList<CameraDescriptor> Cameras,
        IReadOnlyList<PropDescriptor> Props,
        EnvironmentDescriptor? Environment = null,
        IReadOnlyList<GazeDescriptor>? GazeStates = null,
        IReadOnlyList<OverlayDescriptor>? Overlays = null,
        IReadOnlyList<WorldObjectDescriptor>? WorldObjects = null)
    {
        ArgumentNullException.ThrowIfNull(Actors);
        ArgumentNullException.ThrowIfNull(Lights);
        ArgumentNullException.ThrowIfNull(Cameras);
        ArgumentNullException.ThrowIfNull(Props);

        this.Revision = Revision;
        this.Actors = Actors;
        this.Lights = Lights;
        this.Cameras = Cameras;
        this.Props = Props;
        this.Environment = Environment;
        this.GazeStates = GazeStates ?? Array.Empty<GazeDescriptor>();
        this.Overlays = Overlays ?? Array.Empty<OverlayDescriptor>();
        this.WorldObjects =
            WorldObjects ?? Array.Empty<WorldObjectDescriptor>();
    }

    public ulong Revision { get; init; }

    /// <summary>The actor with this exact id, or null.</summary>
    public ActorDescriptor? FindActor(ActorId id)
    {
        foreach (var actor in Actors)
            if (actor.Id.Equals(id))
                return actor;
        return null;
    }

    /// <summary>The actor of this lineage, whatever its generation.</summary>
    public ActorDescriptor? FindActor(Guid lineage)
    {
        foreach (var actor in Actors)
            if (actor.Id.LogicalId == lineage)
                return actor;
        return null;
    }

    public IReadOnlyList<ActorDescriptor> Actors
    {
        get;
        init => field = Freeze(value.Select(actor =>
            actor ?? throw new ArgumentNullException(nameof(Actors))));
    }

    public IReadOnlyList<LightDescriptor> Lights
    {
        get;
        init => field = Freeze(value);
    }

    public IReadOnlyList<CameraDescriptor> Cameras
    {
        get;
        init => field = Freeze(value);
    }

    public IReadOnlyList<PropDescriptor> Props
    {
        get;
        init => field = Freeze(value);
    }

    /// <summary>The staged game-UI overlay nodes. Last of the entity lists and
    /// defaulted empty, so a producer that knows nothing of them — every one
    /// written before they existed — states an empty scene rather than a null
    /// one.</summary>
    public IReadOnlyList<OverlayDescriptor> Overlays
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Freeze(value);
        }
    }

    /// <summary>The adopted world objects. Last of the entity lists and
    /// defaulted empty, exactly as the overlays are, so a producer that knows
    /// nothing of them states an empty scene rather than a null one.</summary>
    public IReadOnlyList<WorldObjectDescriptor> WorldObjects
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Freeze(value);
        }
    }

    public EnvironmentDescriptor? Environment { get; init; }

    public IReadOnlyList<GazeDescriptor> GazeStates
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Freeze(value);
        }
    }

    /// <summary>
    /// Compares the complete snapshot content, including revision and every
    /// descriptor field. SceneSession and the lifecycle use it for
    /// equal-replay detection. It is generated record equality: strings are
    /// ordinal, floats and vectors exact (<c>float.Equals</c>, so NaN equals
    /// NaN), with no tolerances.
    /// </summary>
    public bool ContentEquals(SceneSnapshot? other) =>
        other is not null && Revision == other.Revision && Equals(other);

    public static SceneSnapshot Empty { get; } =
        new(
            0,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            Array.Empty<CameraDescriptor>(),
            Array.Empty<PropDescriptor>());

    public void Deconstruct(
        out ulong revision,
        out IReadOnlyList<ActorDescriptor> actors,
        out IReadOnlyList<LightDescriptor> lights,
        out IReadOnlyList<CameraDescriptor> cameras,
        out IReadOnlyList<PropDescriptor> props)
    {
        revision = Revision;
        actors = Actors;
        lights = Lights;
        cameras = Cameras;
        props = Props;
    }

    public void Deconstruct(
        out ulong revision,
        out IReadOnlyList<ActorDescriptor> actors,
        out IReadOnlyList<LightDescriptor> lights,
        out IReadOnlyList<CameraDescriptor> cameras,
        out IReadOnlyList<PropDescriptor> props,
        out EnvironmentDescriptor? environment,
        out IReadOnlyList<GazeDescriptor> gazeStates)
    {
        Deconstruct(out revision, out actors, out lights, out cameras, out props);
        environment = Environment;
        gazeStates = GazeStates;
    }

    // Descriptors copy their own nested lists into ValueLists on
    // construction, so the snapshot only freezes its top-level lists.
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        ValueList.From(values);
}
