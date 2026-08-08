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
    private readonly IActorSpawnService _spawn;
    private readonly ILightingService _lighting;
    private readonly Dictionary<string, ActorLineage> _lineages =
        new(StringComparer.Ordinal);
    private Dictionary<ActorId, IActor> _actorBindings = new();
    private Dictionary<BoneId, IBone> _boneBindings = new();
    private Dictionary<string, ActorId> _legacyActorIds =
        new(StringComparer.Ordinal);
    private Dictionary<(string Actor, PoseSlot Slot, int Partial, int Index), BoneId>
        _legacyBoneIds = new();
    // Lights are plugin-owned objects with no native identity to re-derive, so
    // their id is minted once per instance and kept by reference identity;
    // generation never advances because the instance dies with the light.
    private Dictionary<ILight, LightId> _lightIds =
        new(LightReferenceComparer.Instance);
    private Dictionary<LightId, ILight> _lightBindings = new();
    private ulong _revision;

    public StableBindingRegistry(
        IActorManager actors,
        ISkeletonService skeletons,
        IActorSpawnService spawn,
        ILightingService lighting)
    {
        _actors = actors;
        _skeletons = skeletons;
        _spawn = spawn;
        _lighting = lighting;
    }

    public SceneSnapshot CurrentSnapshot { get; private set; } =
        SceneSnapshot.Empty;

    public SceneSnapshot Refresh()
    {
        foreach (var lineage in _lineages.Values)
        {
            lineage.Present = false;
            foreach (var slot in lineage.Slots.Values)
                slot.Present = false;
        }

        var actorBindings = new Dictionary<ActorId, IActor>();
        var boneBindings = new Dictionary<BoneId, IBone>();
        var legacyActorIds = new Dictionary<string, ActorId>(
            StringComparer.Ordinal);
        var legacyBoneIds =
            new Dictionary<(string Actor, PoseSlot Slot, int Partial, int Index), BoneId>();
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

            // Every present slot skeleton binds independently: replacing a
            // weapon bumps only that slot's generation and rebuilds only
            // that slot's bone bindings.
            var skeletonDescriptors = new List<SkeletonDescriptor>();
            foreach (var skeleton in _skeletons.GetSkeletons(actor))
            {
                if (!skeleton.IsValid)
                    continue;
                if (!lineage.Slots.TryGetValue(skeleton.Slot, out var slotState))
                {
                    slotState = new SlotState();
                    lineage.Slots.Add(skeleton.Slot, slotState);
                }

                var skeletonKey = skeleton.Id.Unique;
                if (slotState.HasEverBeenPresent &&
                    (!slotState.PresentBeforeScan ||
                     !string.Equals(
                         slotState.LastKey,
                         skeletonKey,
                         StringComparison.Ordinal)))
                    slotState.Generation++;
                slotState.LastKey = skeletonKey;
                slotState.Present = true;
                slotState.HasEverBeenPresent = true;

                var skeletonId = new SkeletonId(
                    actorId,
                    skeleton.Slot,
                    slotState.Generation);
                var bones = new List<BoneDescriptor>(skeleton.Bones.Count);
                foreach (var bone in skeleton.Bones)
                {
                    var boneId = new BoneId(
                        skeletonId,
                        bone.PartialId,
                        bone.BoneIndex,
                        bone.BoneName);
                    if (!boneId.IsValid)
                        continue;
                    boneBindings[boneId] = bone;
                    legacyBoneIds[(
                        legacyKey,
                        skeleton.Slot,
                        bone.PartialId,
                        bone.BoneIndex)] = boneId;

                    BoneId? parent = null;
                    if (bone.ParentBone is { } parentBone)
                    {
                        parent = new BoneId(
                            skeletonId,
                            parentBone.PartialId,
                            parentBone.BoneIndex,
                            parentBone.BoneName);
                    }
                    bones.Add(new BoneDescriptor(
                        boneId,
                        bone.Name,
                        parent,
                        bone.IsHiddenBone));
                }
                skeletonDescriptors.Add(new SkeletonDescriptor(
                    skeletonId,
                    bones));
            }
            actorBindings[actorId] = actor;
            legacyActorIds[legacyKey] = actorId;
            actorDescriptors.Add(new ActorDescriptor(
                actorId,
                actor.Name,
                skeletonDescriptors,
                actor.IsPlayer,
                actor.IsCompanion,
                !_spawn.IsVisible(actor)));
        }

        foreach (var lineage in _lineages.Values)
        {
            lineage.PresentBeforeScan = lineage.Present;
            foreach (var slot in lineage.Slots.Values)
                slot.PresentBeforeScan = slot.Present;
        }

        // Lights present and still valid keep their id; anything else drops
        // out of both maps with this rebuild.
        var lightIds = new Dictionary<ILight, LightId>(
            LightReferenceComparer.Instance);
        var lightBindings = new Dictionary<LightId, ILight>();
        var lightDescriptors = new List<LightDescriptor>();
        foreach (var light in _lighting.Lights)
        {
            if (!light.IsValid)
                continue;
            if (!_lightIds.TryGetValue(light, out var lightId))
                lightId = LightId.New();
            lightIds[light] = lightId;
            lightBindings[lightId] = light;
            lightDescriptors.Add(new LightDescriptor(
                lightId,
                light.Name,
                light.Kind,
                light.IsOn,
                light.Ownership));
        }

        _actorBindings = actorBindings;
        _boneBindings = boneBindings;
        _legacyActorIds = legacyActorIds;
        _legacyBoneIds = legacyBoneIds;
        _lightIds = lightIds;
        _lightBindings = lightBindings;
        CurrentSnapshot = new SceneSnapshot(
            checked(++_revision),
            actorDescriptors,
            lightDescriptors);
        return CurrentSnapshot;
    }

    public ActorId? GetActorId(IActor actor) =>
        _legacyActorIds.TryGetValue(actor.Id.Unique, out var id)
            ? id
            : null;

    public BoneId? GetBoneId(IBone bone)
    {
        var actorKey = bone.Skeleton.Actor.Id.Unique;
        if (!_legacyBoneIds.TryGetValue(
                (actorKey, bone.Skeleton.Slot, bone.PartialId, bone.BoneIndex),
                out var id) ||
            !id.CanonicalName.Equals(
                bone.BoneName,
                StringComparison.Ordinal))
            return null;
        // Exact skeleton instance required: the id must currently bind to
        // THIS bone object. A bone from a released/replaced skeleton shares
        // the reverse key with its replacement but is a different instance —
        // it maps to null, never to the replacement's identity.
        return _boneBindings.TryGetValue(id, out var bound) &&
               ReferenceEquals(bound, bone)
            ? id
            : null;
    }

    public LightId? GetLightId(ILight light) =>
        _lightIds.TryGetValue(light, out var id) &&
        _lightBindings.TryGetValue(id, out var bound) &&
        ReferenceEquals(bound, light)
            ? id
            : null;

    public BindingResult<ILight> Resolve(LightId id)
    {
        if (_lightBindings.TryGetValue(id, out var light) && light.IsValid)
            return new BindingResult<ILight>(
                BindingStatus.Success,
                light);

        foreach (var candidate in _lightBindings.Keys)
        {
            if (candidate.LogicalId != id.LogicalId)
                continue;
            return new BindingResult<ILight>(
                BindingStatus.StaleTarget,
                Detail:
                $"Light generation {id.Generation} is stale; current is {candidate.Generation}.");
        }

        return new BindingResult<ILight>(
            BindingStatus.Missing,
            Detail: $"Light {id.LogicalId:N} is not present.");
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

    /// <summary>Light identity is instance identity: two distinct lights with
    /// identical settings are never the same light.</summary>
    private sealed class LightReferenceComparer : IEqualityComparer<ILight>
    {
        public static LightReferenceComparer Instance { get; } = new();

        public bool Equals(ILight? x, ILight? y) => ReferenceEquals(x, y);

        public int GetHashCode(ILight obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class ActorLineage(Guid logicalId)
    {
        public Guid LogicalId { get; } = logicalId;
        public uint ActorGeneration { get; set; }
        public nint LastAddress { get; set; }
        public bool Present { get; set; }
        public bool PresentBeforeScan { get; set; }
        public bool HasEverBeenPresent { get; set; }
        public Dictionary<PoseSlot, SlotState> Slots { get; } = new();
    }

    /// <summary>Per-slot skeleton lineage: each slot generation advances
    /// independently, so replacing a weapon never invalidates Character or
    /// the other auxiliary slots.</summary>
    private sealed class SlotState
    {
        public uint Generation { get; set; }
        public string? LastKey { get; set; }
        public bool Present { get; set; }
        public bool PresentBeforeScan { get; set; }
        public bool HasEverBeenPresent { get; set; }
    }
}
