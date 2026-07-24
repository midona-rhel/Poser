using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Bindings;

public enum BindingStatus
{
    Success,
    StaleTarget,
    IdentityMismatch,
    Missing,
}

public readonly record struct BindingResult<T>(
    BindingStatus Status,
    T? Value = default,
    string? Detail = null)
    where T : class
{
    public bool Success => Status == BindingStatus.Success && Value != null;
}

/// <summary>
/// Private identity map between domain ids and current legacy/native entities.
/// Refresh and resolution must run on the framework thread.
/// </summary>
public sealed class StableBindingRegistry
{
    private readonly IActorManager _actors;
    private readonly ISkeletonService _skeletons;
    private readonly Dictionary<string, ActorLineage> _lineages =
        new(StringComparer.Ordinal);
    private Dictionary<ActorId, IActor> _actorBindings = new();
    private Dictionary<BoneId, IBone> _boneBindings = new();
    private Dictionary<string, ActorId> _legacyActorIds =
        new(StringComparer.Ordinal);
    private Dictionary<(string Actor, int Partial, int Index), BoneId>
        _legacyBoneIds = new();
    private ulong _revision;

    public StableBindingRegistry(
        IActorManager actors,
        ISkeletonService skeletons)
    {
        _actors = actors;
        _skeletons = skeletons;
    }

    public SceneSnapshot CurrentSnapshot { get; private set; } =
        SceneSnapshot.Empty;

    public SceneSnapshot Refresh()
    {
        foreach (var lineage in _lineages.Values)
        {
            lineage.Present = false;
            lineage.SkeletonPresent = false;
        }

        var actorBindings = new Dictionary<ActorId, IActor>();
        var boneBindings = new Dictionary<BoneId, IBone>();
        var legacyActorIds = new Dictionary<string, ActorId>(
            StringComparer.Ordinal);
        var legacyBoneIds =
            new Dictionary<(string Actor, int Partial, int Index), BoneId>();
        var actorDescriptors = new List<ActorDescriptor>();

        foreach (var actor in _actors.Actors)
        {
            var legacyKey = actor.Id.Unique;
            if (!_lineages.TryGetValue(legacyKey, out var lineage))
            {
                lineage = new ActorLineage(Guid.NewGuid());
                _lineages.Add(legacyKey, lineage);
            }

            if (lineage.HasEverBeenPresent &&
                (!lineage.PresentBeforeScan ||
                 lineage.LastAddress != actor.Address))
                lineage.ActorGeneration++;

            lineage.Present = true;
            lineage.HasEverBeenPresent = true;
            lineage.LastAddress = actor.Address;
            var actorId = new ActorId(
                lineage.LogicalId,
                lineage.ActorGeneration);

            var skeleton = _skeletons.GetSkeleton(actor);
            SkeletonDescriptor? skeletonDescriptor = null;
            if (skeleton is { IsValid: true })
            {
                var skeletonKey = skeleton.Id.Unique;
                if (lineage.HasEverHadSkeleton &&
                    (!lineage.SkeletonPresentBeforeScan ||
                     !string.Equals(
                         lineage.LastSkeletonKey,
                         skeletonKey,
                         StringComparison.Ordinal)))
                    lineage.SkeletonGeneration++;
                lineage.LastSkeletonKey = skeletonKey;
                lineage.SkeletonPresent = true;
                lineage.HasEverHadSkeleton = true;

                var skeletonId = new SkeletonId(
                    actorId,
                    lineage.SkeletonGeneration);
                var bones = new List<BoneDescriptor>(skeleton.Bones.Count);
                foreach (var bone in skeleton.Bones)
                {
                    var boneId = new BoneId(
                        skeletonId,
                        PoseSlot.Character,
                        bone.PartialId,
                        bone.BoneIndex,
                        bone.BoneName);
                    if (!boneId.IsValid)
                        continue;
                    boneBindings[boneId] = bone;
                    legacyBoneIds[(
                        legacyKey,
                        bone.PartialId,
                        bone.BoneIndex)] = boneId;

                    BoneId? parent = null;
                    if (bone.ParentBone is { } parentBone)
                    {
                        parent = new BoneId(
                            skeletonId,
                            PoseSlot.Character,
                            parentBone.PartialId,
                            parentBone.BoneIndex,
                            parentBone.BoneName);
                    }
                    bones.Add(new BoneDescriptor(
                        boneId,
                        bone.Name,
                        parent));
                }
                skeletonDescriptor = new SkeletonDescriptor(
                    skeletonId,
                    bones);
            }
            actorBindings[actorId] = actor;
            legacyActorIds[legacyKey] = actorId;
            actorDescriptors.Add(new ActorDescriptor(
                actorId,
                actor.Name,
                skeletonDescriptor));
        }

        foreach (var lineage in _lineages.Values)
        {
            lineage.PresentBeforeScan = lineage.Present;
            lineage.SkeletonPresentBeforeScan =
                lineage.SkeletonPresent;
        }

        _actorBindings = actorBindings;
        _boneBindings = boneBindings;
        _legacyActorIds = legacyActorIds;
        _legacyBoneIds = legacyBoneIds;
        CurrentSnapshot = new SceneSnapshot(
            checked(++_revision),
            actorDescriptors);
        return CurrentSnapshot;
    }

    public ActorId? GetActorId(IActor actor) =>
        _legacyActorIds.TryGetValue(actor.Id.Unique, out var id)
            ? id
            : null;

    public BoneId? GetBoneId(IBone bone)
    {
        var actorKey = bone.Skeleton.Actor.Id.Unique;
        return _legacyBoneIds.TryGetValue(
            (actorKey, bone.PartialId, bone.BoneIndex),
            out var id) &&
            id.CanonicalName.Equals(
                bone.BoneName,
                StringComparison.Ordinal)
            ? id
            : null;
    }

    public BindingResult<IActor> Resolve(ActorId id)
    {
        if (_actorBindings.TryGetValue(id, out var actor))
            return new BindingResult<IActor>(
                BindingStatus.Success,
                actor);

        var current = _actorBindings.Keys.FirstOrDefault(
            candidate => candidate.LogicalId == id.LogicalId);
        return current.LogicalId == id.LogicalId
            ? new BindingResult<IActor>(
                BindingStatus.StaleTarget,
                Detail: $"Actor generation {id.Generation} is stale; current is {current.Generation}.")
            : new BindingResult<IActor>(
                BindingStatus.Missing,
                Detail: $"Actor {id.LogicalId:N} is not present.");
    }

    public BindingResult<IBone> Resolve(BoneId id)
    {
        if (_boneBindings.TryGetValue(id, out var bone))
            return new BindingResult<IBone>(
                BindingStatus.Success,
                bone);

        var sameIndex = _boneBindings.FirstOrDefault(pair =>
            pair.Key.Skeleton.Actor.LogicalId ==
                id.Skeleton.Actor.LogicalId &&
            pair.Key.Slot == id.Slot &&
            pair.Key.PartialId == id.PartialId &&
            pair.Key.BoneIndex == id.BoneIndex);
        if (sameIndex.Value != null)
        {
            if (!sameIndex.Key.CanonicalName.Equals(
                    id.CanonicalName,
                    StringComparison.Ordinal))
                return new BindingResult<IBone>(
                    BindingStatus.IdentityMismatch,
                    Detail:
                    $"Bone {id.PartialId}:{id.BoneIndex} changed from {id.CanonicalName} to {sameIndex.Key.CanonicalName}.");
            return new BindingResult<IBone>(
                BindingStatus.StaleTarget,
                Detail:
                $"Bone {id.CanonicalName} belongs to a stale actor/skeleton generation.");
        }

        return new BindingResult<IBone>(
            BindingStatus.Missing,
            Detail: $"Bone {id} is not present.");
    }

    private sealed class ActorLineage(Guid logicalId)
    {
        public Guid LogicalId { get; } = logicalId;
        public uint ActorGeneration { get; set; }
        public uint SkeletonGeneration { get; set; }
        public nint LastAddress { get; set; }
        public string? LastSkeletonKey { get; set; }
        public bool Present { get; set; }
        public bool PresentBeforeScan { get; set; }
        public bool HasEverBeenPresent { get; set; }
        public bool SkeletonPresent { get; set; }
        public bool SkeletonPresentBeforeScan { get; set; }
        public bool HasEverHadSkeleton { get; set; }
    }
}
