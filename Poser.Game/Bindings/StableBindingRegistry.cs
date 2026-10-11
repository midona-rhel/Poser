using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Bindings;

/// <summary>
/// One auxiliary body's binding identity, as the registry staged it: the actor
/// itself (<see cref="Skeleton"/> null) and then one entry per present slot
/// skeleton. This is the ONLY account of an auxiliary body the refresh has —
/// they carry no <see cref="ActorDescriptor"/> by design — so it is what
/// <see cref="StableBindingRegistry.AuxiliaryBindingsChanged"/> compares.
/// </summary>
public readonly record struct AuxiliaryBindingKey(
    ActorId Actor, SkeletonId? Skeleton, int Bones);

internal readonly record struct ActorAttachment(
    ActorId Owner,
    CompanionKind Kind);

/// <summary>
/// Private identity map between domain ids and current legacy/native entities.
/// Refresh and resolution must run on the framework thread. Every Resolve
/// enforces that thread and exact-generation identity itself, so callers
/// need not repeat either check.
/// </summary>
public sealed class StableBindingRegistry : IEntityBindings
{
    private readonly IActorManager _actors;
    private readonly ISkeletonService _skeletons;
    private readonly IActorSpawnService _spawn;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly IFramework _framework;
    private Dictionary<string, ActorLineage> _lineages =
        new(StringComparer.Ordinal);
    private BindingCandidate? _stagedCandidate;
    private Dictionary<ActorId, IActor> _actorBindings = new();
    private Dictionary<BoneId, IBone> _boneBindings = new();
    // The first bound bone of each skeleton generation, so ResolveSkeleton
    // does not scan every bone. Rebuilt whenever the bone map is published.
    private Dictionary<SkeletonId, BoneId> _skeletonBones = new();
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
    // Cameras follow the light rule exactly: plugin-owned instances, ids by
    // reference identity, the instance dies with the camera.
    private Dictionary<IVirtualCamera, CameraId> _cameraIds =
        new(ReferenceComparer<IVirtualCamera>.Instance);
    private Dictionary<CameraId, IVirtualCamera> _cameraBindings = new();
    // Props follow the light rule exactly: plugin-owned handles, ids by
    // reference identity, the handle dies with the prop.
    private Dictionary<PropHandle, PropId> _propIds =
        new(ReferenceComparer<PropHandle>.Instance);
    private Dictionary<PropId, PropHandle> _propBindings = new();
    // Overlay nodes follow the light rule exactly: plugin-owned handles, ids by
    // reference identity, the handle dies with the node.
    private Dictionary<OverlayNodeHandle, OverlayId> _overlayIds =
        new(ReferenceComparer<OverlayNodeHandle>.Instance);
    private Dictionary<OverlayId, OverlayNodeHandle> _overlayBindings = new();

    // Adopted world objects follow the light rule exactly: borrowed handles,
    // ids by reference, kept while the claim is live.
    private Dictionary<AdoptedWorldObject, WorldObjectId> _worldObjectIds =
        new(ReferenceComparer<AdoptedWorldObject>.Instance);
    private Dictionary<WorldObjectId, AdoptedWorldObject> _worldObjectBindings =
        new();
    // The auxiliary half of the PUBLISHED maps. Empty until a commit publishes
    // one, which is exactly what makes the first preview body a change.
    private IReadOnlyList<AuxiliaryBindingKey> _auxiliaryBindings =
        Array.Empty<AuxiliaryBindingKey>();

    public StableBindingRegistry(
        IActorManager actors,
        ISkeletonService skeletons,
        IActorSpawnService spawn,
        ILightingService lighting,
        IVirtualCameraService cameras,
        PropSpawnService props,
        OverlayNodeService overlays,
        WorldObjectService worldObjects,
        IFramework framework)
    {
        _framework = framework;
        _actors = actors;
        _skeletons = skeletons;
        _spawn = spawn;
        _lighting = lighting;
        _cameras = cameras;
        _props = props;
        _overlays = overlays;
        _worldObjects = worldObjects;
    }

    private readonly PropSpawnService _props;
    private readonly OverlayNodeService _overlays;
    private readonly WorldObjectService _worldObjects;

    /// <summary>
    /// A framework-thread-only discovery result. Native maps and lineage state
    /// stay private until the owning scene admits this exact candidate.
    /// </summary>
    public sealed class BindingCandidate
    {
        internal BindingCandidate(
            SceneSnapshot snapshot,
            Dictionary<string, ActorLineage> lineages,
            Dictionary<ActorId, IActor> actorBindings,
            Dictionary<BoneId, IBone> boneBindings,
            Dictionary<string, ActorId> legacyActorIds,
            Dictionary<(string Actor, PoseSlot Slot, int Partial, int Index), BoneId> legacyBoneIds,
            Dictionary<ILight, LightId> lightIds,
            Dictionary<LightId, ILight> lightBindings,
            Dictionary<IVirtualCamera, CameraId> cameraIds,
            Dictionary<CameraId, IVirtualCamera> cameraBindings,
            Dictionary<PropHandle, PropId> propIds,
            Dictionary<PropId, PropHandle> propBindings,
            Dictionary<OverlayNodeHandle, OverlayId> overlayIds,
            Dictionary<OverlayId, OverlayNodeHandle> overlayBindings,
            Dictionary<AdoptedWorldObject, WorldObjectId> worldObjectIds,
            Dictionary<WorldObjectId, AdoptedWorldObject> worldObjectBindings,
            IReadOnlyList<AuxiliaryBindingKey> auxiliaryBindings)
        {
            AuxiliaryBindings = auxiliaryBindings;
            Snapshot = snapshot;
            Lineages = lineages;
            ActorBindings = actorBindings;
            BoneBindings = boneBindings;
            LegacyActorIds = legacyActorIds;
            LegacyBoneIds = legacyBoneIds;
            LightIds = lightIds;
            LightBindings = lightBindings;
            CameraIds = cameraIds;
            CameraBindings = cameraBindings;
            PropIds = propIds;
            PropBindings = propBindings;
            OverlayIds = overlayIds;
            OverlayBindings = overlayBindings;
            WorldObjectIds = worldObjectIds;
            WorldObjectBindings = worldObjectBindings;
        }

        public SceneSnapshot Snapshot { get; }

        /// <summary>The bodies this candidate binds that the
        /// <see cref="Snapshot"/> deliberately does not describe.</summary>
        public IReadOnlyList<AuxiliaryBindingKey> AuxiliaryBindings { get; }

        internal Dictionary<string, ActorLineage> Lineages { get; }
        internal Dictionary<ActorId, IActor> ActorBindings { get; }
        internal Dictionary<BoneId, IBone> BoneBindings { get; }
        internal Dictionary<string, ActorId> LegacyActorIds { get; }
        internal Dictionary<(string Actor, PoseSlot Slot, int Partial, int Index), BoneId> LegacyBoneIds { get; }
        internal Dictionary<ILight, LightId> LightIds { get; }
        internal Dictionary<LightId, ILight> LightBindings { get; }
        internal Dictionary<IVirtualCamera, CameraId> CameraIds { get; }
        internal Dictionary<CameraId, IVirtualCamera> CameraBindings { get; }
        internal Dictionary<PropHandle, PropId> PropIds { get; }
        internal Dictionary<PropId, PropHandle> PropBindings { get; }
        internal Dictionary<OverlayNodeHandle, OverlayId> OverlayIds { get; }
        internal Dictionary<OverlayId, OverlayNodeHandle> OverlayBindings { get; }
        internal Dictionary<AdoptedWorldObject, WorldObjectId> WorldObjectIds { get; }
        internal Dictionary<WorldObjectId, AdoptedWorldObject> WorldObjectBindings { get; }
    }

    public BindingCandidate RefreshCandidate()
    {
        if (_stagedCandidate is not null)
            throw new InvalidOperationException("A binding candidate is already staged.");

        var lineages = CloneLineages(_lineages);
        foreach (var lineage in lineages.Values)
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
        var descriptorAddresses = new List<nint>();
        var companionOwners = new Dictionary<nint, ActorAttachment>();
        var auxiliaryBindings = new List<AuxiliaryBindingKey>();

        foreach (var actor in _actors.Actors)
            BindActor(actor, actorDescriptors);

        // Auxiliary bodies (the CharaView preview) get ids and bone bindings
        // so the import pipeline can reach them, but NO scene descriptor: the
        // snapshot is what every pane, picker, and gizmo draws from.
        foreach (var actor in _actors.AuxiliaryActors)
            BindActor(actor, null);

        void BindActor(IActor actor, List<ActorDescriptor>? descriptors)
        {
            if (!_actors.IsAvailable(actor))
                return;
            var legacyKey = actor.Id.Unique;
            if (!lineages.TryGetValue(legacyKey, out var lineage))
            {
                lineage = new ActorLineage(Guid.NewGuid());
                lineages.Add(legacyKey, lineage);
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

                // The slot replacement key is the skeleton CACHE's own
                // invalidation counter: it advances exactly when the native
                // view was rebuilt (issue #78). It used to be a fresh guid
                // per Skeleton OBJECT, which bumped the generation whenever
                // an instance was recreated — wrapper churn looked like a
                // skeleton change and fed the refresh loop.
                var skeletonKey = skeleton.BuildRevision;
                if (slotState.HasEverBeenPresent &&
                    (!slotState.PresentBeforeScan ||
                     slotState.LastKey != skeletonKey))
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
            // A descriptor list of null IS the auxiliary case (the one call
            // site above passes it). Nothing such a body does can move the
            // scene snapshot, so its binding identity is recorded separately or
            // the refresh has no way to know it changed at all.
            if (descriptors is null)
            {
                auxiliaryBindings.Add(new AuxiliaryBindingKey(actorId, null, 0));
                foreach (var skeleton in skeletonDescriptors)
                    auxiliaryBindings.Add(new AuxiliaryBindingKey(
                        actorId, skeleton.Id, skeleton.Bones.Count));
                // Nothing below is an auxiliary body's business: the companion
                // map exists to fill in OwnerActor on DESCRIPTORS, and the
                // address list is read positionally against those descriptors,
                // so an entry with no descriptor may not add one. A CharaView
                // render body owns no companion, mount, or ornament either.
                return;
            }
            descriptors.Add(new ActorDescriptor(
                actorId,
                actor.Name,
                skeletonDescriptors,
                actor.IsPlayer,
                actor.IsCompanion,
                !_spawn.IsVisible(actor),
                IsOwned: _spawn.IsSpawnedActor(actor)
                    || _actors.IsLocalPlayer(actor)
                    || _actors.IsAdopted(actor),
                IsAdopted: _actors.IsAdopted(actor)));
            descriptorAddresses.Add(actor.Address);
            if (!actor.IsCompanion)
                CollectAttachments(actor.Address, actorId, companionOwners);
        }

        LinkCompanionOwners(
            actorDescriptors,
            descriptorAddresses,
            companionOwners);

        foreach (var lineage in lineages.Values)
        {
            lineage.PresentBeforeScan = lineage.Present;
            foreach (var slot in lineage.Slots.Values)
                slot.PresentBeforeScan = slot.Present;
        }

        var describedBoneIds = actorDescriptors
            .SelectMany(actor => actor.Skeletons)
            .SelectMany(skeleton => skeleton.Bones)
            .Select(bone => bone.Id)
            .ToHashSet();

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
            BoneId? attachedBoneId = null;
            if (light.AttachedBone is { } attached &&
                GetBoneId(attached, legacyBoneIds, boneBindings) is { } id &&
                describedBoneIds.Contains(id))
                attachedBoneId = id;
            lightDescriptors.Add(new LightDescriptor(
                lightId,
                light.Name,
                light.Kind,
                light.IsOn,
                light.Ownership,
                attachedBoneId));
        }

        // Cameras keep their id while present and valid, exactly as lights
        // do; the default camera lists first because the service keeps it so.
        var cameraIds = new Dictionary<IVirtualCamera, CameraId>(
            ReferenceComparer<IVirtualCamera>.Instance);
        var cameraBindings = new Dictionary<CameraId, IVirtualCamera>();
        var cameraDescriptors = new List<CameraDescriptor>();
        foreach (var camera in _cameras.Cameras)
        {
            if (!camera.IsValid)
                continue;
            if (!_cameraIds.TryGetValue(camera, out var cameraId))
                cameraId = CameraId.New();
            cameraIds[camera] = cameraId;
            cameraBindings[cameraId] = camera;
            cameraDescriptors.Add(new CameraDescriptor(
                cameraId,
                camera.Name,
                camera.Kind,
                camera.IsLive,
                camera.IsDefault,
                camera.IsLocked));
        }

        // Props keep their id while present and valid, exactly as lights do.
        var propIds = new Dictionary<PropHandle, PropId>(
            ReferenceComparer<PropHandle>.Instance);
        var propBindings = new Dictionary<PropId, PropHandle>();
        var propDescriptors = new List<PropDescriptor>();
        foreach (var prop in _props.Props)
        {
            if (!prop.IsValid)
                continue;
            if (!_propIds.TryGetValue(prop, out var propId))
                propId = PropId.New();
            propIds[prop] = propId;
            propBindings[propId] = prop;
            propDescriptors.Add(new PropDescriptor(
                propId,
                prop.Name,
                prop.Visible));
        }

        // Overlay nodes keep their id while present and valid, exactly as
        // props do. They are the one entity whose row state is a kind rather
        // than a native fact, because a node's kind cannot change.
        var overlayIds = new Dictionary<OverlayNodeHandle, OverlayId>(
            ReferenceComparer<OverlayNodeHandle>.Instance);
        var overlayBindings = new Dictionary<OverlayId, OverlayNodeHandle>();
        var overlayDescriptors = new List<OverlayDescriptor>();
        foreach (var overlay in _overlays.Nodes)
        {
            if (!overlay.IsValid)
                continue;
            if (!_overlayIds.TryGetValue(overlay, out var overlayId))
                overlayId = OverlayId.New();
            overlayIds[overlay] = overlayId;
            overlayBindings[overlayId] = overlay;
            overlayDescriptors.Add(new OverlayDescriptor(
                overlayId,
                overlay.Name,
                overlay.Kind,
                overlay.Visible));
        }

        // Adopted world objects keep their id while the claim is live. A
        // released claim simply stops being listed — its object went back to
        // the map, so there is nothing left for an id to name.
        var worldObjectIds = new Dictionary<AdoptedWorldObject, WorldObjectId>(
            ReferenceComparer<AdoptedWorldObject>.Instance);
        var worldObjectBindings = new Dictionary<WorldObjectId, AdoptedWorldObject>();
        var worldObjectDescriptors = new List<WorldObjectDescriptor>();
        foreach (var worldObject in _worldObjects.Adopted)
        {
            if (!worldObject.IsValid)
                continue;
            if (!_worldObjectIds.TryGetValue(worldObject, out var worldObjectId))
                worldObjectId = WorldObjectId.New();
            worldObjectIds[worldObject] = worldObjectId;
            worldObjectBindings[worldObjectId] = worldObject;
            worldObjectDescriptors.Add(new WorldObjectDescriptor(
                worldObjectId,
                worldObject.Name,
                worldObject.Path,
                worldObject.Visible,
                worldObject.Spawned,
                worldObject.VfxPaused,
                worldObject.AnimationPaused,
                worldObject.NightState));
        }

        // This registry can justify native identity/topology plus the current
        // actor, light, camera, and prop row fields above. The registry cannot
        // truthfully reduce a camera's display-name target and
        // potentially-many tracked bones to one exact actor/bone target, and
        // owns no environment or gaze state. Those relationship fields stay
        // at explicit SceneSnapshot defaults rather than inventing state.
        // Revision zero marks candidate content only; SceneSession's serialized
        // lifecycle integration assigns the admission revision.
        var candidate = new BindingCandidate(
            new SceneSnapshot(
            0,
            actorDescriptors,
            lightDescriptors,
            cameraDescriptors,
            propDescriptors,
            Overlays: overlayDescriptors,
            // Built a dozen lines up and, until now, dropped on the floor: the
            // snapshot defaulted WorldObjects to empty for the plugin's whole
            // life. Everything downstream reads the snapshot, so a borrowed
            // object was adopted, claimed and saved to file while being
            // invisible to the sidebar, to the lifecycle's own signature, and
            // therefore to the binding publish that a pending-select waits on.
            WorldObjects: worldObjectDescriptors),
            lineages,
            actorBindings,
            boneBindings,
            legacyActorIds,
            legacyBoneIds,
            lightIds,
            lightBindings,
            cameraIds,
            cameraBindings,
            propIds,
            propBindings,
            overlayIds,
            overlayBindings,
            worldObjectIds,
            worldObjectBindings,
            auxiliaryBindings);
        _stagedCandidate = candidate;
        return candidate;
    }

    /// <summary>
    /// Everything a candidate reads except the bone walk: per actor its
    /// address, row fields, attachments and each present slot skeleton's
    /// build; per light, camera, prop, overlay and adopted object its instance
    /// and row fields. While this is unchanged a new candidate would equal the
    /// last one, so the idle poll compares it instead of rebuilding. Asking
    /// for the skeletons also lets the cache notice an in-place rebuild,
    /// which it reports by itself.
    /// </summary>
    public List<object?> IdleSignature()
    {
        var signature = new List<object?>();
        AddActors(_actors.Actors);
        signature.Add(null);
        AddActors(_actors.AuxiliaryActors);

        void AddActors(IReadOnlyList<IActor> actors)
        {
            foreach (var actor in actors)
            {
                signature.Add(actor);
                if (!_actors.IsAvailable(actor))
                    continue;
                var (companion, mount, ornament) = Attachments(actor.Address);
                signature.Add(actor.Address);
                signature.Add(actor.Name);
                signature.Add(actor.IsPlayer);
                signature.Add(actor.IsCompanion);
                signature.Add(_spawn.IsVisible(actor));
                signature.Add(_spawn.IsSpawnedActor(actor));
                signature.Add(_actors.IsLocalPlayer(actor));
                signature.Add(_actors.IsAdopted(actor));
                signature.Add(companion);
                signature.Add(mount);
                signature.Add(ornament);
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                {
                    signature.Add(skeleton.Slot);
                    signature.Add(skeleton.IsValid);
                    signature.Add(skeleton.BuildRevision);
                }
            }
        }

        foreach (var light in _lighting.Lights)
        {
            signature.Add(light);
            signature.Add(light.IsValid);
            signature.Add(light.Name);
            signature.Add(light.Kind);
            signature.Add(light.IsOn);
            signature.Add(light.Ownership);
            signature.Add(light.AttachedBone);
        }
        foreach (var camera in _cameras.Cameras)
        {
            signature.Add(camera);
            signature.Add(camera.IsValid);
            signature.Add(camera.Name);
            signature.Add(camera.Kind);
            signature.Add(camera.IsLive);
            signature.Add(camera.IsDefault);
            signature.Add(camera.IsLocked);
        }
        foreach (var prop in _props.Props)
        {
            signature.Add(prop);
            signature.Add(prop.IsValid);
            signature.Add(prop.Name);
            signature.Add(prop.Visible);
        }
        foreach (var overlay in _overlays.Nodes)
        {
            signature.Add(overlay);
            signature.Add(overlay.IsValid);
            signature.Add(overlay.Name);
            signature.Add(overlay.Kind);
            signature.Add(overlay.Visible);
        }
        foreach (var worldObject in _worldObjects.Adopted)
        {
            signature.Add(worldObject);
            signature.Add(worldObject.IsValid);
            signature.Add(worldObject.Name);
            signature.Add(worldObject.Path);
            signature.Add(worldObject.Visible);
            signature.Add(worldObject.Spawned);
            signature.Add(worldObject.VfxPaused);
            signature.Add(worldObject.AnimationPaused);
            signature.Add(worldObject.NightState);
        }
        return signature;
    }

    /// <summary>Entities compare by instance, values by value.</summary>
    public static bool SameIdleSignature(
        IReadOnlyList<object?>? previous,
        IReadOnlyList<object?> current)
    {
        if (previous is null || previous.Count != current.Count)
            return false;
        for (int i = 0; i < current.Count; i++)
        {
            var (a, b) = (previous[i], current[i]);
            if (!ReferenceEquals(a, b) && !(a is ValueType or string && Equals(a, b)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Forgets every actor lineage. Lineages are keyed per native object and
    /// slot index, so they only accumulate within a GPose session; once the
    /// session's actors are gone none of them can be matched again.
    /// </summary>
    public void ResetLineages()
    {
        _lineages = new Dictionary<string, ActorLineage>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Publishes the exact staged maps after scene admission. The structural
    /// comparison matters for NoChange: a native refresh may only replace maps
    /// when every published id and generation still names this scene.
    /// </summary>
    public void CommitCandidate(
        BindingCandidate candidate,
        SceneSnapshot admittedSnapshot)
    {
        EnsureStaged(candidate);
        if (!candidate.Snapshot.ContentEquals(admittedSnapshot with { Revision = 0 }))
            throw new InvalidOperationException(
                "Binding candidate does not match the admitted scene.");
        _lineages = candidate.Lineages;
        _actorBindings = candidate.ActorBindings;
        _boneBindings = candidate.BoneBindings;
        // Insertion order: the same first bone the old full scan found.
        var skeletonBones = new Dictionary<SkeletonId, BoneId>();
        foreach (var boneId in _boneBindings.Keys)
            skeletonBones.TryAdd(boneId.Skeleton, boneId);
        _skeletonBones = skeletonBones;
        _legacyActorIds = candidate.LegacyActorIds;
        _legacyBoneIds = candidate.LegacyBoneIds;
        _lightIds = candidate.LightIds;
        _lightBindings = candidate.LightBindings;
        _cameraIds = candidate.CameraIds;
        _cameraBindings = candidate.CameraBindings;
        ReconcileCameraTargets();
        _propIds = candidate.PropIds;
        _propBindings = candidate.PropBindings;
        _overlayIds = candidate.OverlayIds;
        _overlayBindings = candidate.OverlayBindings;
        _worldObjectIds = candidate.WorldObjectIds;
        _worldObjectBindings = candidate.WorldObjectBindings;
        _auxiliaryBindings = candidate.AuxiliaryBindings;
        _stagedCandidate = null;
    }

    /// <summary>Binding admission is the authority for actor generations.
    /// Clear camera follow state here, including while its pane is hidden, so
    /// a stale target cannot keep applying an old offset or name.</summary>
    private void ReconcileCameraTargets()
    {
        foreach (var camera in _cameraBindings.Values)
        {
            if (camera.TargetActorId is { } targetId)
            {
                if (IsCurrentCameraTarget(
                        targetId, camera.TargetActor, _actorBindings))
                    continue;
                _cameras.ClearTargetActor(camera);
                continue;
            }

            if (HasCameraTargetResidual(
                    camera.TargetActor, camera.TargetActorName,
                    camera.TargetOffset, camera.IsTargetLocked))
                _cameras.ClearTargetActor(camera);
        }
    }

    /// <summary>Binding admission retains follow state only when its native
    /// actor reference is the exact object for the same generation. A
    /// same-id replacement is stale and is never rebound.</summary>
    internal static bool IsCurrentCameraTarget(
        ActorId targetId,
        IActor? retainedTarget,
        IReadOnlyDictionary<ActorId, IActor> actorBindings)
    {
        return retainedTarget is not null &&
            actorBindings.TryGetValue(targetId, out var currentTarget) &&
            ReferenceEquals(retainedTarget, currentTarget);
    }

    /// <summary>Any retained follow component or lock is residual state when
    /// the exact target id is absent.</summary>
    internal static bool HasCameraTargetResidual(
        IActor? retainedTarget,
        string targetName,
        Vector3 targetOffset,
        bool targetLocked = false)
    {
        return retainedTarget is not null || targetName.Length > 0 ||
            targetOffset != Vector3.Zero || targetLocked;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> binds a different set of auxiliary
    /// bodies than the published maps do.
    ///
    /// <para>The scene snapshot cannot answer this and must not be made to: an
    /// auxiliary body (the CharaView pose preview at object index 441) is bound
    /// so the import pipeline can reach it and deliberately given NO
    /// <see cref="ActorDescriptor"/>, because the snapshot is what every pane,
    /// picker, and gizmo draws from. The consequence is that the preview body
    /// appearing changes NOTHING the scene signature can see, so the refresh
    /// that coalesces on that signature aborts the very candidate carrying the
    /// preview's actor and bone bindings — and
    /// <see cref="GetActorId"/> answers null for the preview body forever.
    /// Every pose stated against it is then dropped by the one guard that asks
    /// (<c>PosePreviewService.TryApplyPendingPose</c>) without a word: the
    /// CharaView renders a perfectly good body standing in its idle stance.
    /// This predicate is the second signature that case needs.</para>
    /// </summary>
    public bool AuxiliaryBindingsChanged(BindingCandidate candidate)
    {
        EnsureStaged(candidate);
        return !candidate.AuxiliaryBindings.SequenceEqual(_auxiliaryBindings);
    }

    /// <summary>Discards a candidate whose scene admission did not commit.</summary>
    public void AbortCandidate(BindingCandidate candidate)
    {
        EnsureStaged(candidate);
        _stagedCandidate = null;
    }

    private void EnsureStaged(BindingCandidate candidate)
    {
        if (!ReferenceEquals(_stagedCandidate, candidate))
            throw new InvalidOperationException("Binding candidate is not staged.");
    }

    private static Dictionary<string, ActorLineage> CloneLineages(
        IReadOnlyDictionary<string, ActorLineage> source)
    {
        var clone = new Dictionary<string, ActorLineage>(
            source.Count,
            StringComparer.Ordinal);
        foreach (var (key, lineage) in source)
        {
            var copy = new ActorLineage(lineage.LogicalId)
            {
                ActorGeneration = lineage.ActorGeneration,
                LastAddress = lineage.LastAddress,
                Present = lineage.Present,
                PresentBeforeScan = lineage.PresentBeforeScan,
                HasEverBeenPresent = lineage.HasEverBeenPresent,
            };
            foreach (var (slot, state) in lineage.Slots)
                copy.Slots[slot] = new SlotState
                {
                    Generation = state.Generation,
                    LastKey = state.LastKey,
                    Present = state.Present,
                    PresentBeforeScan = state.PresentBeforeScan,
                    HasEverBeenPresent = state.HasEverBeenPresent,
                };
            clone.Add(key, copy);
        }
        return clone;
    }

    /// <summary>
    /// Reads the three attachment pointers a character can hold and records
    /// each target address against its owner. Only characters are scanned and
    /// only their own fields are read: no native sibling or child chain is
    /// ever traversed, so the map stays a one-level companion→owner relation.
    /// </summary>
    private static void CollectAttachments(
        nint address,
        ActorId owner,
        Dictionary<nint, ActorAttachment> companionOwners)
    {
        var (companion, mount, ornament) = Attachments(address);
        if (companion != nint.Zero)
            companionOwners[companion] = new(owner, CompanionKind.Companion);
        if (mount != nint.Zero)
            companionOwners[mount] = new(owner, CompanionKind.Mount);
        if (ornament != nint.Zero)
            companionOwners[ornament] = new(owner, CompanionKind.Ornament);
    }

    private static unsafe (nint Companion, nint Mount, nint Ornament) Attachments(
        nint address)
    {
        if (address == nint.Zero)
            return default;
        var native = (Character*)address;
        if (native->ChildObject == null)
            return default;
        return (
            (nint)native->CompanionData.CompanionObject,
            (nint)native->Mount.MountObject,
            (nint)native->OrnamentData.OrnamentObject);
    }

    /// <summary>Projects the native owner pointers onto the already-minted
    /// actor identities. Both the exact owner generation and slot kind travel
    /// together; an unrelated or self-referential address remains a root.</summary>
    internal static void LinkCompanionOwners(
        List<ActorDescriptor> descriptors,
        List<nint> addresses,
        Dictionary<nint, ActorAttachment> companionOwners)
    {
        if (descriptors.Count != addresses.Count)
            throw new ArgumentException(
                "Actor descriptors and native addresses must be positional peers.");
        if (companionOwners.Count == 0)
            return;
        for (int i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (!descriptor.IsCompanion)
                continue;
            if (companionOwners.TryGetValue(addresses[i], out var attachment) &&
                !attachment.Owner.Equals(descriptor.Id))
                descriptors[i] = descriptor with
                {
                    OwnerActor = attachment.Owner,
                    AttachmentKind = attachment.Kind,
                };
        }
    }

    /// <summary>Returns an actor id only for the exact currently bound
    /// instance. A released pointer can share the native unique key with its
    /// replacement, but must never resolve to that replacement.</summary>
    public ActorId? GetActorId(IActor actor)
    {
        if (!_legacyActorIds.TryGetValue(actor.Id.Unique, out var id) ||
            !_actorBindings.TryGetValue(id, out var bound) ||
            !ReferenceEquals(bound, actor))
            return null;
        return id;
    }

    public BoneId? GetBoneId(IBone bone) =>
        GetBoneId(bone, _legacyBoneIds, _boneBindings);

    private static BoneId? GetBoneId(
        IBone bone,
        IReadOnlyDictionary<
            (string Actor, PoseSlot Slot, int Partial, int Index),
            BoneId> legacyBoneIds,
        IReadOnlyDictionary<BoneId, IBone> boneBindings)
    {
        var actorKey = bone.Skeleton.Actor.Id.Unique;
        if (!legacyBoneIds.TryGetValue(
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
        return boneBindings.TryGetValue(id, out var bound) &&
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

    public CameraId? GetCameraId(IVirtualCamera camera) =>
        _cameraIds.TryGetValue(camera, out var id) &&
        _cameraBindings.TryGetValue(id, out var bound) &&
        ReferenceEquals(bound, camera)
            ? id
            : null;

    public PropId? GetPropId(PropHandle prop) =>
        _propIds.TryGetValue(prop, out var id) &&
        _propBindings.TryGetValue(id, out var bound) &&
        ReferenceEquals(bound, prop)
            ? id
            : null;

    public WorldObjectId? GetWorldObjectId(AdoptedWorldObject worldObject) =>
        _worldObjectIds.TryGetValue(worldObject, out var id) &&
        _worldObjectBindings.TryGetValue(id, out var bound) &&
        ReferenceEquals(bound, worldObject)
            ? id
            : null;

    public OverlayId? GetOverlayId(OverlayNodeHandle overlay) =>
        _overlayIds.TryGetValue(overlay, out var id) &&
        _overlayBindings.TryGetValue(id, out var bound) &&
        ReferenceEquals(bound, overlay)
            ? id
            : null;

    // ── Resolution ───────────────────────────────────────────────────
    // Each kind keeps its own liveness rule and stale-vs-missing detail; the
    // thread and identity checks live once, in the generic helper.

    public BindingResult<IActor> Resolve(ActorId id) => Resolve("Actor", id, _actorBindings,
        static (r, _, actor) => r._actors.IsAvailable(actor) ? null
            : Missing<IActor>("The actor's native body is no longer available."),
        static (r, actor) => r.GetActorId(actor), static (r, id) => r.AbsentActor(id));

    public BindingResult<IBone> Resolve(BoneId id) => Resolve("Bone", id, _boneBindings,
        static (r, id, bone) => r.RefuseBone(id, bone),
        static (r, bone) => r.GetBoneId(bone), static (r, id) => r.AbsentBone(id));

    public BindingResult<ILight> Resolve(LightId id) => Resolve("Light", id, _lightBindings,
        static (r, id, light) => light.IsValid ? null : r.AbsentLight(id),
        static (r, light) => r.GetLightId(light), static (r, id) => r.AbsentLight(id));

    public BindingResult<IVirtualCamera> Resolve(CameraId id) => Resolve("Camera", id, _cameraBindings,
        static (r, id, camera) => camera.IsValid ? null : r.AbsentCamera(id),
        static (r, camera) => r.GetCameraId(camera), static (r, id) => r.AbsentCamera(id));

    public BindingResult<PropHandle> Resolve(PropId id) => Resolve("Object", id, _propBindings,
        static (_, id, prop) => prop.IsValid ? null : AbsentHandle<PropHandle>("Object", id.LogicalId),
        static (r, prop) => r.GetPropId(prop),
        static (_, id) => AbsentHandle<PropHandle>("Object", id.LogicalId));

    public BindingResult<AdoptedWorldObject> Resolve(WorldObjectId id) => Resolve("World object", id,
        _worldObjectBindings,
        static (_, id, world) => world.IsValid ? null
            : AbsentHandle<AdoptedWorldObject>("World object", id.LogicalId),
        static (r, world) => r.GetWorldObjectId(world),
        static (_, id) => AbsentHandle<AdoptedWorldObject>("World object", id.LogicalId));

    public BindingResult<OverlayNodeHandle> Resolve(OverlayId id) => Resolve("Overlay", id, _overlayBindings,
        static (_, id, overlay) => overlay.IsValid ? null
            : AbsentHandle<OverlayNodeHandle>("Overlay", id.LogicalId),
        static (r, overlay) => r.GetOverlayId(overlay),
        static (_, id) => AbsentHandle<OverlayNodeHandle>("Overlay", id.LogicalId));

    /// <summary>The one resolve path. Off the framework thread the maps may be
    /// mid-swap and the entity mid-teardown, so it fails closed with
    /// <see cref="BindingStatus.WrongThread"/>. A bound, live entity must also
    /// map back to exactly this id: an instance listed twice by its service in
    /// one refresh is minted two ids, and only the last one is its own.</summary>
    private BindingResult<T> Resolve<TId, T>(
        string kind,
        TId id,
        Dictionary<TId, T> bindings,
        Func<StableBindingRegistry, TId, T, BindingResult<T>?> refuse,
        Func<StableBindingRegistry, T, TId?> reverse,
        Func<StableBindingRegistry, TId, BindingResult<T>> absent)
        where TId : struct, IEquatable<TId>
        where T : class
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return new BindingResult<T>(BindingStatus.WrongThread,
                Detail: $"{kind} {id} can only be resolved on the framework thread.");
        if (!bindings.TryGetValue(id, out var value))
            return absent(this, id);
        if (refuse(this, id, value) is { } refusal)
            return refusal;
        return reverse(this, value) is { } bound && bound.Equals(id)
            ? new BindingResult<T>(BindingStatus.Success, value)
            : new BindingResult<T>(BindingStatus.IdentityMismatch,
                Detail: $"{kind} {id} is bound under another id.");
    }

    private static BindingResult<T> Missing<T>(string detail) where T : class =>
        new(BindingStatus.Missing, Detail: detail);

    private static BindingResult<T> AbsentHandle<T>(string kind, Guid logicalId) where T : class =>
        Missing<T>($"{kind} {logicalId:N} is not present.");

    private BindingResult<IActor> AbsentActor(ActorId id)
    {
        foreach (var current in _actorBindings.Keys)
            if (current.LogicalId == id.LogicalId)
                return new BindingResult<IActor>(BindingStatus.StaleTarget,
                    Detail: $"Actor generation {id.Generation} is stale; current is {current.Generation}.");
        return Missing<IActor>($"Actor {id.LogicalId:N} is not present.");
    }

    private BindingResult<ILight> AbsentLight(LightId id)
    {
        foreach (var current in _lightBindings.Keys)
            if (current.LogicalId == id.LogicalId)
                return new BindingResult<ILight>(BindingStatus.StaleTarget,
                    Detail: $"Light generation {id.Generation} is stale; current is {current.Generation}.");
        return Missing<ILight>($"Light {id.LogicalId:N} is not present.");
    }

    private BindingResult<IVirtualCamera> AbsentCamera(CameraId id)
    {
        foreach (var current in _cameraBindings.Keys)
            if (current.LogicalId == id.LogicalId)
                return new BindingResult<IVirtualCamera>(BindingStatus.StaleTarget,
                    Detail: $"Camera generation {id.Generation} is stale; current is {current.Generation}.");
        return Missing<IVirtualCamera>($"Camera {id.LogicalId:N} is not present.");
    }

    /// <summary>A bound bone is live only while its actor resolves and that
    /// actor's current slot skeleton still holds this exact bone.</summary>
    private BindingResult<IBone>? RefuseBone(BoneId id, IBone bone)
    {
        var actor = Resolve(id.Skeleton.Actor);
        if (!actor.Success)
            return new BindingResult<IBone>(actor.Status, Detail: actor.Detail);
        var current = _skeletons.GetSkeleton(actor.Value!, id.Slot);
        if (current is { IsValid: true } &&
            ReferenceEquals(current.GetBone(id.PartialId, id.BoneIndex), bone))
            return null;
        return new BindingResult<IBone>(BindingStatus.StaleTarget,
            Detail: "The native skeleton changed since this bone was bound.");
    }

    private BindingResult<IBone> AbsentBone(BoneId id)
    {
        foreach (var key in _boneBindings.Keys)
        {
            if (key.Skeleton.Actor.LogicalId != id.Skeleton.Actor.LogicalId ||
                key.Slot != id.Slot ||
                key.PartialId != id.PartialId ||
                key.BoneIndex != id.BoneIndex)
                continue;
            return key.CanonicalName.Equals(id.CanonicalName, StringComparison.Ordinal)
                ? new BindingResult<IBone>(BindingStatus.StaleTarget,
                    Detail: $"Bone {id.CanonicalName} belongs to a stale actor/skeleton generation.")
                : new BindingResult<IBone>(BindingStatus.IdentityMismatch,
                    Detail: $"Bone {id.PartialId}:{id.BoneIndex} changed from {id.CanonicalName} to {key.CanonicalName}.");
        }
        return Missing<IBone>($"Bone {id} is not present.");
    }

    /// <summary>The live skeleton behind an exact skeleton generation, reached
    /// through its first bound bone — the registry keys on bones, and a
    /// skeleton with no bound bone is one nothing can be asked about.</summary>
    public ISkeleton? ResolveSkeleton(SkeletonId id) =>
        _skeletonBones.TryGetValue(id, out var bone) ? Resolve(bone).Value?.Skeleton : null;

    /// <summary>The current-generation id for a target whose actor is still
    /// in the scene: the same bone by slot, partial, index and name on the
    /// redrawn body, or the actor's current id. Null when nothing matches.
    /// Used only on reconcile, so the scans are not per frame.</summary>
    public TransformTargetId? CurrentTarget(TransformTargetId target)
    {
        if (target.Collider is { } collider)
            foreach (var pair in _overlayBindings)
                if (pair.Key.LogicalId == collider.LogicalId && pair.Value.State.Collider != null)
                    return TransformTargetId.ForCollider(pair.Key);
        switch (target.Kind)
        {
            case TransformTargetKind.Bone when target.Bone is { } bone:
                foreach (var pair in _boneBindings)
                {
                    var key = pair.Key;
                    if (key.Skeleton.Actor.LogicalId == bone.Skeleton.Actor.LogicalId
                        && key.Skeleton.Slot == bone.Skeleton.Slot
                        && key.PartialId == bone.PartialId
                        && key.BoneIndex == bone.BoneIndex
                        && key.CanonicalName.Equals(bone.CanonicalName, StringComparison.Ordinal))
                        return TransformTargetId.ForBone(key);
                }
                return null;
            case TransformTargetKind.Actor when target.Actor is { } actor:
                foreach (var pair in _boneBindings)
                {
                    var current = pair.Key.Skeleton.Actor;
                    if (current.LogicalId == actor.LogicalId)
                        return TransformTargetId.ForActor(current);
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Light identity is instance identity: two distinct lights with
    /// identical settings are never the same light.</summary>
    // The port's handle-typed forms. The registry keeps its concrete
    // signatures for the runtime; the surfaces see the interfaces.
    PropId? IEntityBindings.GetPropId(IPropHandle prop) => GetPropId((PropHandle)prop);

    WorldObjectId? IEntityBindings.GetWorldObjectId(IWorldObject worldObject) =>
        GetWorldObjectId((AdoptedWorldObject)worldObject);

    OverlayId? IEntityBindings.GetOverlayId(IOverlayNode overlay) =>
        GetOverlayId((OverlayNodeHandle)overlay);

    BindingResult<IPropHandle> IEntityBindings.Resolve(PropId id)
    {
        var result = Resolve(id);
        return new(result.Status, result.Value, result.Detail);
    }

    BindingResult<IWorldObject> IEntityBindings.Resolve(WorldObjectId id)
    {
        var result = Resolve(id);
        return new(result.Status, result.Value, result.Detail);
    }

    BindingResult<IOverlayNode> IEntityBindings.Resolve(OverlayId id)
    {
        var result = Resolve(id);
        return new(result.Status, result.Value, result.Detail);
    }

    private sealed class LightReferenceComparer : IEqualityComparer<ILight>
    {
        public static LightReferenceComparer Instance { get; } = new();

        public bool Equals(ILight? x, ILight? y) => ReferenceEquals(x, y);

        public int GetHashCode(ILight obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>Instance identity for any plugin-owned entity — the camera's
    /// twin of the light comparer.</summary>
    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    internal sealed class ActorLineage(Guid logicalId)
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
    internal sealed class SlotState
    {
        public uint Generation { get; set; }
        public long? LastKey { get; set; }
        public bool Present { get; set; }
        public bool PresentBeforeScan { get; set; }
        public bool HasEverBeenPresent { get; set; }
    }
}
