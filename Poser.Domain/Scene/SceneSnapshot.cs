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
/// Actor information for the scene list, including attached skeletons.
/// OwnerActor and AttachmentKind identify a companion's attachment when known.
/// IsAdopted identifies a borrowed world actor; IsOwned controls eligibility to export appearance.
/// </summary>
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

    /// <summary>
    /// The body skeleton, or null if absent. Use GetSkeleton for weapon and other slots.
    /// </summary>
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
/// Light information for the scene list, with an optional bone attachment.
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
/// Camera information for the scene list, including lock state and target.
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
/// Prop name and visibility for the scene list.
/// </summary>
public sealed record PropDescriptor(
    PropId Id,
    string Name,
    bool Visible = true);

/// <summary>
/// World-object information for the scene list. Path identifies its game asset
/// without retaining a game-memory address.
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
/// Overlay name, type, and visibility for the scene list.
/// Text, colors, and placement are stored separately in the overlay editor state.
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
/// Observed time, weather, and environment sections held by Poser.
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
/// An actor's gaze mode, targets, and lock settings, without game-memory pointers.
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
/// Scene information shared with application readers, without native pointers.
/// Lists use ValueList so record equality compares their contents rather than list objects.
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

    /// <summary>
    /// Finds the logical actor regardless of its current game-object version.
    /// </summary>
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

    /// <summary>
    /// Scene overlays. Omitted constructor input becomes an empty list.
    /// </summary>
    public IReadOnlyList<OverlayDescriptor> Overlays
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Freeze(value);
        }
    }

    /// <summary>
    /// World objects in the scene. Omitted constructor input becomes an empty list.
    /// </summary>
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
    /// Compares revision and all stored content with record equality, without numeric tolerances.
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
