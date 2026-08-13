using System.Numerics;
using Poser.Domain.Identity;

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
    public PoseSlot Slot => Id.Slot;
}

/// <summary>
/// Owns the pointer-free facts for one actor and its present slot skeletons.
/// It does not own native handles, transforms, or scene indexing.
/// </summary>
/// <param name="OwnerActor">The exact actor generation this companion is
/// attached to, when that relationship is known. The companion remains its
/// own actor and state owner.</param>
public sealed record ActorDescriptor(
    ActorId Id,
    string Name,
    IReadOnlyList<SkeletonDescriptor> Skeletons,
    bool IsPlayer = false,
    bool IsCompanion = false,
    bool IsHidden = false,
    ActorId? OwnerActor = null)
{
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
/// owns no native entities and carries no application indexes; SceneSession is
/// the sole owner of indexing, stale resolution, and revision acceptance.
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
        IReadOnlyList<GazeDescriptor>? GazeStates = null)
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
    }

    public ulong Revision { get; init; }

    public IReadOnlyList<ActorDescriptor> Actors
    {
        get;
        init => field = Freeze(value.Select(CopyActor));
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

    public EnvironmentDescriptor? Environment { get; init; }

    public IReadOnlyList<GazeDescriptor> GazeStates
    {
        get;
        init => field = Freeze(value);
    }

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

    private static ActorDescriptor CopyActor(ActorDescriptor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor with
        {
            Skeletons = Freeze(actor.Skeletons.Select(skeleton =>
                skeleton with { Bones = Freeze(skeleton.Bones) })),
        };
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.ToArray());
    }
}
