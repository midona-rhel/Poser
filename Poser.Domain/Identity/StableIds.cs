namespace Poser.Domain.Identity;

/// <summary>One logical actor at one exact native binding generation.</summary>
public readonly record struct ActorId(Guid LogicalId, uint Generation)
{
    public static ActorId New() => new(Guid.NewGuid(), 0);
    public ActorId NextGeneration() => new(LogicalId, checked(Generation + 1));
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
/// One exact skeleton generation of one slot belonging to one exact actor
/// generation. Slots are independently replaceable native skeletons of the
/// SAME actor: replacing a weapon bumps only that slot's generation.
/// </summary>
public readonly record struct SkeletonId(
    ActorId Actor,
    PoseSlot Slot,
    uint Generation)
{
    public SkeletonId NextGeneration() =>
        new(Actor, Slot, checked(Generation + 1));
    public override string ToString() => $"{Actor}/{Slot}/s{Generation}";
}

/// <summary>
/// Stable bone identity. Partial/index is the native lookup key and canonical
/// name is an independent mismatch guard. The slot lives ONLY on the owning
/// skeleton id, so bone and skeleton slots can never disagree.
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

    public override string ToString() =>
        $"{Skeleton}/{PartialId}:{BoneIndex}:{CanonicalName}";
}

/// <summary>One spawned scene light at one exact native binding generation.</summary>
public readonly record struct LightId(Guid LogicalId, uint Generation)
{
    public static LightId New() => new(Guid.NewGuid(), 0);
    public LightId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>One spawned scene prop at one exact native binding generation.</summary>
public readonly record struct PropId(Guid LogicalId, uint Generation)
{
    public static PropId New() => new(Guid.NewGuid(), 0);
    public PropId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>One virtual camera at one exact native binding generation.</summary>
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
}

/// <summary>Which gaze point a gaze-target selection addresses. Anchor is the
/// shared point every enabled unlocked part follows; the parts are the
/// individually divergeable per-part points.</summary>
public enum GazePart { Anchor, Eyes, Head, Body }

/// <summary>Stable selection identity for application state.</summary>
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
        PropId? prop = null)
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

    /// <summary>The actor's gaze point in Position mode; selectable so the
    /// world gizmo can own it.</summary>
    public static SelectionId ForGazeTarget(ActorId actor, GazePart part = GazePart.Anchor) =>
        new(SceneEntityKind.GazeTarget, actor, null, null, null, gaze: part);

    public override string ToString() => Kind switch
    {
        SceneEntityKind.Actor => $"actor:{Actor}",
        SceneEntityKind.Bone when Bone is { } bone => $"bone:{bone}",
        SceneEntityKind.Bone => $"bone-group:{OwnerActorLineage:N}:{ExternalId}",
        SceneEntityKind.Light => $"light:{Light}",
        SceneEntityKind.Camera => $"camera:{Camera}",
        SceneEntityKind.Prop => $"prop:{Prop}",
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
}

/// <summary>The subset of selection identities that can enter a transform gesture.</summary>
public readonly record struct TransformTargetId
{
    private TransformTargetId(
        TransformTargetKind kind,
        ActorId? actor,
        BoneId? bone,
        LightId? light = null,
        PropId? prop = null)
    {
        Kind = kind;
        Actor = actor;
        Bone = bone;
        Light = light;
        Prop = prop;
    }

    public TransformTargetKind Kind { get; }
    public ActorId? Actor { get; }
    public BoneId? Bone { get; }
    public LightId? Light { get; }
    public PropId? Prop { get; }
    public Guid ActorLineage =>
        Actor?.LogicalId ??
        Bone?.Skeleton.Actor.LogicalId ??
        Guid.Empty;

    public static TransformTargetId ForActor(ActorId actor) =>
        new(TransformTargetKind.Actor, actor, null);

    public static TransformTargetId ForBone(BoneId bone) =>
        new(TransformTargetKind.Bone, null, bone);

    public static TransformTargetId ForLight(LightId light) =>
        new(TransformTargetKind.Light, null, null, light);

    public static TransformTargetId ForProp(PropId prop) =>
        new(TransformTargetKind.Prop, null, null, null, prop);

    public SelectionId ToSelectionId() => Kind switch
    {
        TransformTargetKind.Actor => SelectionId.ForActor(Actor!.Value),
        TransformTargetKind.Bone => SelectionId.ForBone(Bone!.Value),
        TransformTargetKind.Light => SelectionId.ForLight(Light!.Value),
        TransformTargetKind.Prop => SelectionId.ForProp(Prop!.Value),
        _ => throw new InvalidOperationException($"Unknown target kind {Kind}."),
    };

    public override string ToString() => ToSelectionId().ToString();
}
