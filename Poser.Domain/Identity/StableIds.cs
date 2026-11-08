namespace Poser.Domain.Identity;

/// <summary>
/// Identifies an actor and its current game-object version.
/// The logical ID stays the same when the runtime replaces the object; Generation changes.
/// </summary>
public readonly record struct ActorId(Guid LogicalId, uint Generation)
{
    public static ActorId New() => new(Guid.NewGuid(), 0);
    public ActorId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override int GetHashCode() => HashCode.Combine(LogicalId, Generation);
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

public enum PoseSlot
{
    Character,
    MainHand,
    OffHand,
    Prop,
    Ornament,
    Unknown,
}

/// <summary>
/// Identifies a body, weapon, or other skeleton attached to an actor.
/// Each slot has its own version, so replacing a weapon need not invalidate body bones.
/// </summary>
public readonly record struct SkeletonId(
    ActorId Actor,
    PoseSlot Slot,
    uint Generation)
{
    public SkeletonId NextGeneration() =>
        new(Actor, Slot, checked(Generation + 1));
    public override int GetHashCode() => HashCode.Combine(Actor, Slot, Generation);
    public override string ToString() => $"{Actor}/{Slot}/s{Generation}";
}

/// <summary>
/// Identifies a bone by skeleton version, partial skeleton, index, and game name.
/// The runtime uses the indices to find it and the name to check the result.
/// </summary>
public readonly record struct BoneId(
    SkeletonId Skeleton,
    int PartialId,
    int BoneIndex,
    string CanonicalName)
{
    public PoseSlot Slot => Skeleton.Slot;

    public bool IsValid =>
        PartialId >= 0 &&
        BoneIndex >= 0 &&
        Skeleton.Slot != PoseSlot.Unknown &&
        !string.IsNullOrWhiteSpace(CanonicalName);

    // Partial/index are the native lookup discriminators. CanonicalName is
    // still compared by the generated Equals implementation as an exact
    // mismatch guard, but hashing it makes every lookup re-hash the string.
    public override int GetHashCode() =>
        HashCode.Combine(Skeleton, PartialId, BoneIndex);

    public override string ToString() =>
        $"{Skeleton}/{PartialId}:{BoneIndex}:{CanonicalName}";
}

/// <summary>
/// Identifies a scene light and its current runtime version.
/// </summary>
public readonly record struct LightId(Guid LogicalId, uint Generation)
{
    public static LightId New() => new(Guid.NewGuid(), 0);
    public LightId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>
/// Identifies a spawned prop and its current runtime version.
/// </summary>
public readonly record struct PropId(Guid LogicalId, uint Generation)
{
    public static PropId New() => new(Guid.NewGuid(), 0);
    public PropId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>
/// Identifies a map object temporarily controlled by Poser.
/// Unlike a spawned prop, it must be restored to the map when released, not destroyed.
/// </summary>
public readonly record struct WorldObjectId(Guid LogicalId, uint Generation)
{
    public static WorldObjectId New() => new(Guid.NewGuid(), 0);
    public WorldObjectId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>
/// Identifies a scene overlay and its current runtime version.
/// </summary>
public readonly record struct OverlayId(Guid LogicalId, uint Generation)
{
    public static OverlayId New() => new(Guid.NewGuid(), 0);
    public OverlayId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>
/// Identifies a virtual camera and its current runtime version.
/// </summary>
public readonly record struct CameraId(Guid LogicalId, uint Generation)
{
    public static CameraId New() => new(Guid.NewGuid(), 0);
    public CameraId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

public enum SceneEntityKind
{
    Actor,
    Bone,
    Light,
    Camera,
    Environment,
    GazeTarget,
    Prop,

    /// <summary>A staged overlay. Native UI panels use screen placement;
    /// IK colliders use world transforms and the normal transform history.</summary>
    Overlay,

    /// <summary>
    /// A map object temporarily controlled by Poser and restored when released.
    /// </summary>
        WorldObject,
}

/// <summary>
/// Selects the shared gaze point or an independent eyes, head, or body point.
/// </summary>
public enum GazePart { Anchor, Eyes, Head, Body }

/// <summary>
/// Identifies the selected object or bone group without storing a game-memory pointer.
/// </summary>
public readonly record struct SelectionId
{
    private SelectionId(
        SceneEntityKind kind,
        ActorId? actor,
        BoneId? bone,
        string? externalId,
        Guid? ownerActorLineage = null,
        LightId? light = null,
        GazePart? gaze = null,
        CameraId? camera = null,
        PropId? prop = null,
        OverlayId? overlay = null,
        WorldObjectId? worldObject = null)
    {
        Kind = kind;
        Actor = actor;
        Bone = bone;
        ExternalId = externalId;
        OwnerActorLineage = ownerActorLineage;
        Light = light;
        Gaze = gaze;
        Camera = camera;
        Prop = prop;
        Overlay = overlay;
        WorldObject = worldObject;
    }

    public SceneEntityKind Kind { get; }
    public ActorId? Actor { get; }
    public BoneId? Bone { get; }
    public string? ExternalId { get; }
    public Guid? OwnerActorLineage { get; }
    public LightId? Light { get; }
    public GazePart? Gaze { get; }
    public CameraId? Camera { get; }
    public PropId? Prop { get; }
    public OverlayId? Overlay { get; }
    public WorldObjectId? WorldObject { get; }

    /// <summary>The actor a selection edits: the actor itself, a bone's
    /// owner, or a gaze anchor's owner. Null for every other kind.</summary>
    public ActorId? OwningActor => Kind switch
    {
        SceneEntityKind.Actor => Actor,
        SceneEntityKind.Bone => Bone?.Skeleton.Actor,
        SceneEntityKind.GazeTarget => Actor,
        _ => null,
    };

    public Guid? ActorLineage =>
        Actor?.LogicalId ??
        Bone?.Skeleton.Actor.LogicalId ??
        OwnerActorLineage;

    public static SelectionId ForActor(ActorId actor) =>
        new(SceneEntityKind.Actor, actor, null, null);

    public static SelectionId ForBone(BoneId bone) =>
        new(SceneEntityKind.Bone, null, bone, null);

    public static SelectionId ForLight(LightId light) =>
        new(SceneEntityKind.Light, null, null, null, light: light);

    public static SelectionId ForCamera(CameraId camera) =>
        new(SceneEntityKind.Camera, null, null, null, camera: camera);

    public static SelectionId ForProp(PropId prop) =>
        new(SceneEntityKind.Prop, null, null, null, prop: prop);

    public static SelectionId ForOverlay(OverlayId overlay) =>
        new(SceneEntityKind.Overlay, null, null, null, overlay: overlay);

    public static SelectionId ForWorldObject(WorldObjectId worldObject) =>
        new(
            SceneEntityKind.WorldObject, null, null, null,
            worldObject: worldObject);

    public static SelectionId ForBoneGroup(ActorId actor, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Bone group id is required.", nameof(id));
        return new SelectionId(
            SceneEntityKind.Bone,
            null,
            null,
            id,
            actor.LogicalId);
    }

    /// <summary>The scene's one and only environment; it has no owning actor.</summary>
    public static SelectionId ForEnvironment() =>
        new(SceneEntityKind.Environment, null, null, null);

    /// <summary>
    /// Makes a gaze point selectable for movement with the world gizmo.
    /// </summary>
    public static SelectionId ForGazeTarget(ActorId actor, GazePart part = GazePart.Anchor) =>
        new(SceneEntityKind.GazeTarget, actor, null, null, null, gaze: part);

    public override int GetHashCode() => Kind switch
    {
        SceneEntityKind.Actor => HashCode.Combine(Kind, Actor),
        SceneEntityKind.Bone when Bone is { } bone =>
            HashCode.Combine(Kind, bone),
        SceneEntityKind.Bone =>
            HashCode.Combine(Kind, OwnerActorLineage, ExternalId),
        SceneEntityKind.Light => HashCode.Combine(Kind, Light),
        SceneEntityKind.Camera => HashCode.Combine(Kind, Camera),
        SceneEntityKind.Prop => HashCode.Combine(Kind, Prop),
        SceneEntityKind.Overlay => HashCode.Combine(Kind, Overlay),
        SceneEntityKind.WorldObject => HashCode.Combine(Kind, WorldObject),
        SceneEntityKind.GazeTarget => HashCode.Combine(Kind, Actor, Gaze),
        SceneEntityKind.Environment => Kind.GetHashCode(),
        _ => HashAllFields(),
    };

    private int HashAllFields()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Actor);
        hash.Add(Bone);
        hash.Add(ExternalId);
        hash.Add(OwnerActorLineage);
        hash.Add(Light);
        hash.Add(Gaze);
        hash.Add(Camera);
        hash.Add(Prop);
        hash.Add(Overlay);
        hash.Add(WorldObject);
        return hash.ToHashCode();
    }

    public override string ToString() => Kind switch
    {
        SceneEntityKind.Actor => $"actor:{Actor}",
        SceneEntityKind.Bone when Bone is { } bone => $"bone:{bone}",
        SceneEntityKind.Bone => $"bone-group:{OwnerActorLineage:N}:{ExternalId}",
        SceneEntityKind.Light => $"light:{Light}",
        SceneEntityKind.Camera => $"camera:{Camera}",
        SceneEntityKind.Prop => $"prop:{Prop}",
        SceneEntityKind.Overlay => $"overlay:{Overlay}",
        SceneEntityKind.WorldObject => $"world-object:{WorldObject}",
        SceneEntityKind.Environment => "environment",
        SceneEntityKind.GazeTarget => $"gaze:{Actor}:{Gaze}",
        _ => throw new InvalidOperationException($"Unknown selection kind {Kind}."),
    };
}

public enum TransformTargetKind
{
    Actor,
    Bone,
    Light,
    Prop,
    WorldObject,
    Collider,
}

/// <summary>
/// Identifies an object that can be moved, rotated, or scaled by a transform gesture.
/// </summary>
public readonly record struct TransformTargetId
{
    private TransformTargetId(
        TransformTargetKind kind,
        ActorId? actor,
        BoneId? bone,
        LightId? light = null,
        PropId? prop = null,
        WorldObjectId? worldObject = null,
        OverlayId? collider = null)
    {
        Kind = kind;
        Actor = actor;
        Bone = bone;
        Light = light;
        Prop = prop;
        WorldObject = worldObject;
        Collider = collider;
    }

    public TransformTargetKind Kind { get; }
    public OverlayId? Collider { get; }
    public ActorId? Actor { get; }
    public BoneId? Bone { get; }
    public LightId? Light { get; }
    public PropId? Prop { get; }
    public WorldObjectId? WorldObject { get; }
    public Guid ActorLineage =>
        Actor?.LogicalId ??
        Bone?.Skeleton.Actor.LogicalId ??
        Guid.Empty;

    public static TransformTargetId ForActor(ActorId actor) =>
        new(TransformTargetKind.Actor, actor, null);

    public static TransformTargetId ForCollider(OverlayId collider) =>
        new(TransformTargetKind.Collider, null, null, collider: collider);

    public static TransformTargetId ForBone(BoneId bone) =>
        new(TransformTargetKind.Bone, null, bone);

    public static TransformTargetId ForLight(LightId light) =>
        new(TransformTargetKind.Light, null, null, light);

    public static TransformTargetId ForProp(PropId prop) =>
        new(TransformTargetKind.Prop, null, null, null, prop);

    public static TransformTargetId ForWorldObject(WorldObjectId worldObject) =>
        new(
            TransformTargetKind.WorldObject, null, null, null, null,
            worldObject);

    public SelectionId ToSelectionId() => Kind switch
    {
        TransformTargetKind.Collider => SelectionId.ForOverlay(Collider!.Value),
        TransformTargetKind.Actor => SelectionId.ForActor(Actor!.Value),
        TransformTargetKind.Bone => SelectionId.ForBone(Bone!.Value),
        TransformTargetKind.Light => SelectionId.ForLight(Light!.Value),
        TransformTargetKind.Prop => SelectionId.ForProp(Prop!.Value),
        TransformTargetKind.WorldObject =>
            SelectionId.ForWorldObject(WorldObject!.Value),
        _ => throw new InvalidOperationException($"Unknown target kind {Kind}."),
    };

    public override string ToString() => ToSelectionId().ToString();
}
