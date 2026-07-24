namespace Poser.Domain.Identity;

/// <summary>One logical actor at one exact native binding generation.</summary>
public readonly record struct ActorId(Guid LogicalId, uint Generation)
{
    public static ActorId New() => new(Guid.NewGuid(), 0);
    public ActorId NextGeneration() => new(LogicalId, checked(Generation + 1));
    public override string ToString() => $"{LogicalId:N}@{Generation}";
}

/// <summary>One exact skeleton generation belonging to one exact actor generation.</summary>
public readonly record struct SkeletonId(ActorId Actor, uint Generation)
{
    public SkeletonId NextGeneration() => new(Actor, checked(Generation + 1));
    public override string ToString() => $"{Actor}/s{Generation}";
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
/// Stable bone identity. Partial/index is the native lookup key and canonical
/// name is an independent mismatch guard.
/// </summary>
public readonly record struct BoneId(
    SkeletonId Skeleton,
    PoseSlot Slot,
    int PartialId,
    int BoneIndex,
    string CanonicalName)
{
    public bool IsValid =>
        PartialId >= 0 &&
        BoneIndex >= 0 &&
        !string.IsNullOrWhiteSpace(CanonicalName);

    public override string ToString() =>
        $"{Skeleton}/{Slot}/{PartialId}:{BoneIndex}:{CanonicalName}";
}

public enum SceneEntityKind
{
    Actor,
    Bone,
}

/// <summary>Stable selection identity for application state.</summary>
public readonly record struct SelectionId
{
    private SelectionId(
        SceneEntityKind kind,
        ActorId? actor,
        BoneId? bone,
        string? externalId,
        Guid? ownerActorLineage = null)
    {
        Kind = kind;
        Actor = actor;
        Bone = bone;
        ExternalId = externalId;
        OwnerActorLineage = ownerActorLineage;
    }

    public SceneEntityKind Kind { get; }
    public ActorId? Actor { get; }
    public BoneId? Bone { get; }
    public string? ExternalId { get; }
    public Guid? OwnerActorLineage { get; }

    public Guid? ActorLineage =>
        Actor?.LogicalId ??
        Bone?.Skeleton.Actor.LogicalId ??
        OwnerActorLineage;

    public static SelectionId ForActor(ActorId actor) =>
        new(SceneEntityKind.Actor, actor, null, null);

    public static SelectionId ForBone(BoneId bone) =>
        new(SceneEntityKind.Bone, null, bone, null);

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

    public override string ToString() => Kind switch
    {
        SceneEntityKind.Actor => $"actor:{Actor}",
        SceneEntityKind.Bone when Bone is { } bone => $"bone:{bone}",
        SceneEntityKind.Bone => $"bone-group:{OwnerActorLineage:N}:{ExternalId}",
        _ => throw new InvalidOperationException($"Unknown selection kind {Kind}."),
    };
}

public enum TransformTargetKind
{
    Actor,
    Bone,
}

/// <summary>The subset of selection identities that can enter a transform gesture.</summary>
public readonly record struct TransformTargetId
{
    private TransformTargetId(
        TransformTargetKind kind,
        ActorId? actor,
        BoneId? bone)
    {
        Kind = kind;
        Actor = actor;
        Bone = bone;
    }

    public TransformTargetKind Kind { get; }
    public ActorId? Actor { get; }
    public BoneId? Bone { get; }
    public Guid ActorLineage =>
        Actor?.LogicalId ??
        Bone?.Skeleton.Actor.LogicalId ??
        Guid.Empty;

    public static TransformTargetId ForActor(ActorId actor) =>
        new(TransformTargetKind.Actor, actor, null);

    public static TransformTargetId ForBone(BoneId bone) =>
        new(TransformTargetKind.Bone, null, bone);

    public SelectionId ToSelectionId() => Kind switch
    {
        TransformTargetKind.Actor => SelectionId.ForActor(Actor!.Value),
        TransformTargetKind.Bone => SelectionId.ForBone(Bone!.Value),
        _ => throw new InvalidOperationException($"Unknown target kind {Kind}."),
    };

    public override string ToString() => ToSelectionId().ToString();
}
