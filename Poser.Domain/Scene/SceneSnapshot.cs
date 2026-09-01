using System.Numerics;
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
    bool AnimPaused = false);

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
/// integration is serialized. Generated record equality remains reference
/// based for collection properties; use <see cref="ContentEquals"/> when
/// scene content equality is required.
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
    /// Compares the complete snapshot content, including revision and nested
    /// collection values. Generated record equality is intentionally not used
    /// for scene admission because <see cref="IReadOnlyList{T}"/> equality is
    /// reference-based; SceneSession uses this structural comparison for
    /// equal-replay detection.
    /// </summary>
    public bool ContentEquals(SceneSnapshot? other)
    {
        if (other is null || Revision != other.Revision)
            return false;

        return ActorsEqual(Actors, other.Actors) &&
               LightsEqual(Lights, other.Lights) &&
               CamerasEqual(Cameras, other.Cameras) &&
               PropsEqual(Props, other.Props) &&
               OverlaysEqual(Overlays, other.Overlays) &&
               WorldObjectsEqual(WorldObjects, other.WorldObjects) &&
               EnvironmentEqual(Environment, other.Environment) &&
               GazeEqual(GazeStates, other.GazeStates);
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

    private static bool ActorsEqual(
        IReadOnlyList<ActorDescriptor> left,
        IReadOnlyList<ActorDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(first.Name, second.Name) ||
                first.IsPlayer != second.IsPlayer ||
                first.IsCompanion != second.IsCompanion ||
                first.IsHidden != second.IsHidden ||
                first.OwnerActor != second.OwnerActor ||
                !SkeletonsEqual(first.Skeletons, second.Skeletons))
                return false;
        }

        return true;
    }

    private static bool SkeletonsEqual(
        IReadOnlyList<SkeletonDescriptor> left,
        IReadOnlyList<SkeletonDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !BonesEqual(first.Bones, second.Bones))
                return false;
        }

        return true;
    }

    private static bool BonesEqual(
        IReadOnlyList<BoneDescriptor> left,
        IReadOnlyList<BoneDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(
                    first.DisplayName,
                    second.DisplayName) ||
                first.Parent != second.Parent ||
                first.IsHidden != second.IsHidden)
                return false;
        }

        return true;
    }

    private static bool LightsEqual(
        IReadOnlyList<LightDescriptor> left,
        IReadOnlyList<LightDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(first.Name, second.Name) ||
                first.Kind != second.Kind ||
                first.IsOn != second.IsOn ||
                first.Ownership != second.Ownership ||
                first.AttachedBone != second.AttachedBone)
                return false;
        }

        return true;
    }

    private static bool CamerasEqual(
        IReadOnlyList<CameraDescriptor> left,
        IReadOnlyList<CameraDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(first.Name, second.Name) ||
                first.Kind != second.Kind ||
                first.IsLive != second.IsLive ||
                first.IsDefault != second.IsDefault ||
                first.IsLocked != second.IsLocked ||
                first.TargetActor != second.TargetActor ||
                first.TargetBone != second.TargetBone ||
                first.TargetOffset != second.TargetOffset)
                return false;
        }

        return true;
    }

    private static bool PropsEqual(
        IReadOnlyList<PropDescriptor> left,
        IReadOnlyList<PropDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(first.Name, second.Name) ||
                first.Visible != second.Visible)
                return false;
        }

        return true;
    }

    private static bool WorldObjectsEqual(
        IReadOnlyList<WorldObjectDescriptor> left,
        IReadOnlyList<WorldObjectDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(first.Name, second.Name) ||
                !StringComparer.Ordinal.Equals(first.Path, second.Path) ||
                first.Visible != second.Visible)
                return false;
        }

        return true;
    }

    private static bool OverlaysEqual(
        IReadOnlyList<OverlayDescriptor> left,
        IReadOnlyList<OverlayDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Id != second.Id ||
                !StringComparer.Ordinal.Equals(first.Name, second.Name) ||
                first.Kind != second.Kind ||
                first.Visible != second.Visible)
                return false;
        }

        return true;
    }

    private static bool EnvironmentEqual(
        EnvironmentDescriptor? left,
        EnvironmentDescriptor? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.MinuteOfDay == right.MinuteOfDay &&
               left.DayOfMonth == right.DayOfMonth &&
               left.WeatherId == right.WeatherId &&
               left.IsTimeFrozen == right.IsTimeFrozen &&
               left.IsWeatherOverrideEnabled ==
                   right.IsWeatherOverrideEnabled &&
               left.HeldSections == right.HeldSections;
    }

    private static bool GazeEqual(
        IReadOnlyList<GazeDescriptor> left,
        IReadOnlyList<GazeDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Actor != second.Actor ||
                first.Mode != second.Mode ||
                first.Parts != second.Parts ||
                first.LockedParts != second.LockedParts ||
                first.TargetActor != second.TargetActor ||
                first.Anchor != second.Anchor ||
                first.EyesPosition != second.EyesPosition ||
                first.HeadPosition != second.HeadPosition ||
                first.BodyPosition != second.BodyPosition)
                return false;
        }

        return true;
    }
}
