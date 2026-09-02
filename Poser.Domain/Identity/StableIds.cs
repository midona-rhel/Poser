namespace Poser.Domain.Identity;

/// <summary>One logical actor at one exact native binding generation.</summary>
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
    public override int GetHashCode() => HashCode.Combine(Actor, Slot, Generation);
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

    // Partial/index are the native lookup discriminators. CanonicalName is
    // still compared by the generated Equals implementation as an exact
    // mismatch guard, but hashing it makes every lookup re-hash the string.
    public override int GetHashCode() =>
        HashCode.Combine(Skeleton, PartialId, BoneIndex);

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

/// <summary>One ADOPTED world object — a BG/layout object the map placed and
/// the user took into the scene — at one exact native binding generation. It is
/// deliberately its own identity rather than a prop's: a prop is Poser's own
/// object and is DESTROYED, while this one is borrowed and is RESTORED, and
/// nothing that can act on both may confuse the two.</summary>
public readonly record struct WorldObjectId(Guid LogicalId, uint Generation)
{
    public static WorldObjectId New() => new(Guid.NewGuid(), 0);
    public WorldObjectId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>One staged overlay node — a game-UI dialogue box, chat bubble or
/// status line — at one exact native binding generation.</summary>
public readonly record struct OverlayId(Guid LogicalId, uint Generation)
{
    public static OverlayId New() => new(Guid.NewGuid(), 0);
    public OverlayId NextGeneration() => new(LogicalId, checked(Generation + 1));
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

    /// <summary>A staged game-UI overlay node. It is a scene entity like a
    /// prop, but a FLAT one: it lives in screen space, so it never enters the
    /// world gizmo or the transform history.</summary>
    Overlay,

    /// <summary>An adopted BG/layout object: the map's own furniture, borrowed
    /// into the scene. It transforms like a prop and is REMOVED like a borrowed
    /// light — released back to where it stood, never destroyed.</summary>
    WorldObject,
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

    /// <summary>The actor's gaze point in Position mode; selectable so the
    /// world gizmo can own it.</summary>
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
}

/// <summary>The subset of selection identities that can enter a transform gesture.</summary>
public readonly record struct TransformTargetId
{
    private TransformTargetId(
        TransformTargetKind kind,
        ActorId? actor,
        BoneId? bone,
        LightId? light = null,
        PropId? prop = null,
        WorldObjectId? worldObject = null)
    {
        Kind = kind;
        Actor = actor;
        Bone = bone;
        Light = light;
        Prop = prop;
        WorldObject = worldObject;
    }

    public TransformTargetKind Kind { get; }
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
