using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Domain.Identity;
using Poser.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Service for manipulating bone transforms using game hooks.
/// Simple delta-based system like Brio - bones rotate around themselves.
/// </summary>
public unsafe class BonePosingService : IBonePosingService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IIKService _ikService;
    private readonly Poser.Game.Bindings.StableBindingRegistry _bindings;
    private readonly IPosingService _posingService;
    private readonly Poser.Config.ConfigurationService _configuration;

    /// <summary>Poses parked across a redraw, keyed by stable actor identity
    /// rather than by any of the three things a rebuild invalidates (address,
    /// skeleton instance, draw object).</summary>

    /// <summary>Ktisis's "position root": the first REAL bone of partial 0, the
    /// only bone whose position delta is restored after a rebuild. Poser's
    /// <c>IsSkeletonRoot</c> is the parentless <c>n_root</c> instead, whose
    /// position is the actor's world placement — carrying that would fight the
    /// model transform override, so it is deliberately not used here.</summary>
    private const string PositionRootBoneName = "n_hara";

    // Hook for intercepting bone physics updates
    private delegate nint UpdateBonePhysicsDelegate(nint a1);
    private readonly Hook<UpdateBonePhysicsDelegate>? _updateBonePhysicsHook;

    // Hook for finalizing skeletons before rendering (takes final snapshot)
    private delegate void FinalizeSkeletonsDelegate(nint a1);
    private readonly Hook<FinalizeSkeletonsDelegate>? _finalizeSkeletonsHook;

    /// <summary>
    /// EXACT runtime skeleton identity: actor address, slot, and the built
    /// skeleton instance's unique id. Replacing a slot creates a new
    /// skeleton instance, so the replacement can never inherit the old
    /// skeleton's pose state through a reused (address, slot) pair.
    /// </summary>
    /// <summary>
    /// A pose store's address: the ACTOR and the SLOT, by name. Deliberately
    /// not the skeleton instance and not the actor's pointer.
    ///
    /// <para>This key used to carry <c>skeleton.Id.Unique</c>, a fresh id per
    /// Skeleton object. A redraw builds a new Skeleton, so the authored pose
    /// ended up filed under a key nothing would look up again — and every
    /// piece of machinery that existed to survive a redraw (the carryover
    /// parking lot, the two adoption points, the migration in the
    /// skeleton-created handler) existed only to move poses from the dead key
    /// to the live one. Keyed by name, the store simply stays where it is and
    /// the next apply pass lands it on whatever skeleton is current.</para>
    ///
    /// <para>The bone stacks inside were already name-keyed
    /// (<c>SkeletonPoseInfo.GetPoseInfo(name, partial)</c>). Only the outer key
    /// was instance-bound.</para>
    /// </summary>
    private readonly record struct SkeletonKey(
        string Actor,
        PoseSlot Slot)
    {
        public static SkeletonKey Of(ISkeleton skeleton) => new(
            skeleton.Actor.Id.Unique,
            skeleton.Slot);
    }

    // Pose info per (actor, slot) — never per skeleton instance.
    private readonly Dictionary<SkeletonKey, SkeletonPoseInfo> _poseInfos = new();

    // Track which slot skeletons need updating this frame (have modifications)
    private readonly HashSet<SkeletonKey> _skeletonsToUpdate = new();

    // Track which slot skeletons need cache updates (visible overlays, active gizmo, etc.)
    private readonly HashSet<SkeletonKey> _skeletonsToUpdateCache = new();

    /// <summary>One-frame apply-pass leases taken by
    /// <see cref="RequestRawTransformRefresh"/>; cleared by every rebuild, so
    /// a caller that still needs live raw asks again next tick.</summary>
    private readonly HashSet<SkeletonKey> _rawRefreshRequests = new();

    /// <summary>
    /// Actions registered from OUTSIDE the apply pass to run INSIDE it, once,
    /// per bone — Brio's <c>SkeletonPosingCapability._transitiveActions</c>
    /// (Capabilities/Posing/SkeletonPosingCapability.cs:35). Brio keeps the
    /// list on the per-actor capability and clears it when the posing interval
    /// ends (SkeletonPosingCapability.cs:238-241, raised from
    /// SkeletonService.EndPosingInverval, SkeletonService.cs:375-379); Poser
    /// keys it by the exact slot-skeleton instance the caller registered
    /// against, so a replaced skeleton can never inherit another's batch.
    /// </summary>
    private sealed class TransitiveActionSet
    {
        public required ISkeleton Skeleton;
        public readonly List<Action<IBone, BonePoseInfo>> Actions = new();

        /// <summary>Set by the pass that ran the actions. False at interval
        /// end means the batch was dropped without ever executing — Brio has
        /// no counterpart because Brio's pass visits every registered
        /// skeleton unconditionally.</summary>
        public bool Executed;
    }

    private readonly Dictionary<SkeletonKey, TransitiveActionSet> _transitiveActions = new();

    /// <summary>Session IK state per exact endpoint: validated chain
    /// configuration, resolved native chain, and the Fixed-mode capture.
    /// Keyed by the exact skeleton instance, so a replacement never
    /// inherits configuration or targets.</summary>
    private sealed class IkChainState
    {
        public required Poser.Domain.Posing.IkChainConfig Config;
        public Poser.Domain.Posing.IkResolvedChain Chain;
        public (Vector3 Target, Vector3 Translation)? FixedCapture;
    }

    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), IkChainState>
        _ikChains = new();

    // Native-boundary observations used by the live acceptance harness.
    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), BoneEvaluationObservation>
        _evaluationObservations = new();
    private long _evaluationSequence;

    /// <summary>Reused snapshot buffers for the two per-frame passes that must
    /// iterate a collection they may mutate. Both are single-threaded (physics
    /// detour / framework update) and never nested, so one instance each keeps
    /// the steady state free of per-frame arrays.</summary>
    private readonly List<SkeletonKey> _updatePassBuffer = new();
    private readonly List<(SkeletonKey Skeleton, int Partial, int Bone)>
        _observationRemovalBuffer = new();

    /// <summary>Reused snapshot buffers for the finalize pass — same hazard,
    /// same idiom as <see cref="_updatePassBuffer"/>: UpdateSkeletonCache →
    /// GetSkeleton can publish SkeletonChangedEvent synchronously, whose
    /// handler mutates BOTH live sets (PurgeSkeletonState / re-Add), which
    /// would throw mid-enumeration inside the FinalizeSkeletons native frame.
    /// Single-threaded (render detour), never nested.</summary>
    private readonly List<SkeletonKey> _finalizePassBuffer = new();
    private readonly List<SkeletonKey> _finalizeCachePassBuffer = new();

    private bool _isUpdating = false;

    // One-shot fault flags: the detours run every frame, so a repeating
    // fault must not turn the log into a firehose.
    private bool _physicsDetourFaultLogged;
    private bool _finalizeDetourFaultLogged;

    public BonePosingService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IActorManager actorManager,
        IEventBus eventBus,
        IIKService ikService,
        Poser.Game.Bindings.StableBindingRegistry bindings,
        IPosingService posingService,
        Poser.Config.ConfigurationService configuration,
        IGameInteropProvider hooking,
        ISigScanner scanner)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _ikService = ikService;
        _bindings = bindings;
        _posingService = posingService;
        _configuration = configuration;

        // Hook UpdateBonePhysics - this is called during skeleton updates
        try
        {
            var updateBonePhysicsAddress = scanner.ScanText("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4");
            _updateBonePhysicsHook = hooking.HookFromAddress<UpdateBonePhysicsDelegate>(updateBonePhysicsAddress, UpdateBonePhysicsDetour);
            _updateBonePhysicsHook.Enable();
            _log.Debug("BonePosingService: UpdateBonePhysics hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"BonePosingService: Failed to hook UpdateBonePhysics: {ex.Message}");
        }

        // Hook FinalizeSkeletons - called before rendering, takes final snapshot
        try
        {
            var finalizeSkeletonsAddress = scanner.ScanText("40 53 57 41 54 41 55 48 83 EC ?? ?? 48 ?? ?? ?? ?? ?? ?? ?? 4C") /* Brio 0.8 sig; JMP in Framework.TaskRenderGraphicsRender */;
            _finalizeSkeletonsHook = hooking.HookFromAddress<FinalizeSkeletonsDelegate>(finalizeSkeletonsAddress, FinalizeSkeletonsDetour);
            _finalizeSkeletonsHook.Enable();
            _log.Debug("BonePosingService: FinalizeSkeletons hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"BonePosingService: Failed to hook FinalizeSkeletons: {ex.Message}");
        }

        _framework.Update += OnFrameworkUpdate;
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);

        _log.Debug("BonePosingService initialized");
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        var result = _updateBonePhysicsHook!.Original(a1);

        if (!_gPoseService.IsGPosing || _isUpdating)
            return result;

        _isUpdating = true;
        try
        {
            _evaluationSequence++;
            ApplyAllBoneTransforms();
        }
        catch (Exception ex)
        {
            // Never fault the native physics update (CharacterFinalizeDetour
            // standard): a managed fault here would unwind into the game's
            // render graph.
            if (!_physicsDetourFaultLogged)
            {
                _physicsDetourFaultLogged = true;
                _log.Error($"BonePosingService: apply pass faulted (logged once): {ex}");
            }
        }
        finally
        {
            _isUpdating = false;
        }

        return result;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _skeletonsToUpdate.Clear();

        foreach (var (slotKey, poseInfo) in _poseInfos)
        {
            // A registered batch qualifies the skeleton on its own. Brio gets
            // this for free — its pass takes every skeleton that has a posing
            // capability (SkeletonService.cs:227-231) — while Poser's pass is
            // opt-in per stack/chain, and a bake registers its actions exactly
            // when it has just cleared both.
            if (poseInfo.IsOverridden || HasEnabledChains(slotKey) ||
                _transitiveActions.ContainsKey(slotKey) ||
                _rawRefreshRequests.Contains(slotKey))
            {
                _skeletonsToUpdate.Add(slotKey);
                continue;
            }

            RemoveEvaluationObservations(slotKey);
        }

        _skeletonsToUpdateCache.Clear();
        // The lease is one rebuild long. A settling bake re-requests on every
        // tick it waits; when it stops, the skeleton leaves the pass again.
        _rawRefreshRequests.Clear();
    }

    /// <summary>
    /// Brio's <c>SkeletonPosingCapability.RegisterTransitiveAction</c>
    /// (SkeletonPosingCapability.cs:52-55). The action runs once for every
    /// bone of this slot skeleton, inside the physics-detour apply pass, at
    /// the point where the bone's existing stacks have been applied and its
    /// transform caches refreshed — see
    /// <see cref="ApplyTransformsWithPerBoneUpdate"/>.
    /// </summary>
    public void RegisterTransitiveAction(
        ISkeleton skeleton,
        Action<IBone, BonePoseInfo> action)
    {
        var key = SkeletonKey.Of(skeleton);
        if (!_transitiveActions.TryGetValue(key, out var set))
            _transitiveActions[key] = set =
                new TransitiveActionSet { Skeleton = skeleton };
        set.Actions.Add(action);

        // Materialize the pose store so the per-frame rebuild can see the
        // skeleton at all, and register directly for the pass that is still
        // ahead of us this frame (registration from a framework update
        // precedes this frame's detour; registration from UI draw follows it
        // and is picked up by the next rebuild).
        GetPoseInfo(skeleton);
        _skeletonsToUpdate.Add(key);
    }

    public event Action<TransitiveActionOutcome>? TransitiveActionsEnded;

    /// <summary>Brio's <c>SkeletonPosingCapability.ExecuteTransitiveActions</c>
    /// (SkeletonPosingCapability.cs:57-60).</summary>
    private static void ExecuteTransitiveActions(
        TransitiveActionSet set,
        IBone bone,
        BonePoseInfo poseInfo)
    {
        var actions = set.Actions;
        for (var i = 0; i < actions.Count; i++)
            actions[i](bone, poseInfo);
    }

    /// <summary>
    /// Brio's <c>SkeletonService.EndPosingInverval</c> → <c>SkeletonUpdateEnd</c>
    /// → <c>SkeletonPosingCapability.OnSkeletonUpdateEnd</c>: every registered
    /// batch is dropped when the interval ends, whether or not a pass consumed
    /// it. Poser reports the outcome so a caller that needs to know its actions
    /// ran (the IK bake, which owes a history entry) is never left waiting.
    /// </summary>
    private void EndTransitiveActions()
    {
        if (_transitiveActions.Count == 0)
            return;
        var ended = _transitiveActions.Values.ToArray();
        _transitiveActions.Clear();
        foreach (var set in ended)
            RaiseTransitiveActionsEnded(set);
    }

    private void RaiseTransitiveActionsEnded(TransitiveActionSet set)
    {
        try
        {
            TransitiveActionsEnded?.Invoke(
                new TransitiveActionOutcome(set.Skeleton, set.Executed));
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"BonePosingService: transitive action outcome handler threw: {ex.Message}");
        }
    }

    public void RegisterSkeletonForCacheUpdate(ISkeleton skeleton)
    {
        _skeletonsToUpdateCache.Add(SkeletonKey.Of(skeleton));
    }

    /// <summary>
    /// One frame's membership in the apply pass, for a caller that needs
    /// <see cref="IBone.LastRawTransform"/> refreshed on a skeleton that
    /// carries nothing the pass would otherwise select it for. The pose store
    /// is materialized because the rebuild in <see cref="OnFrameworkUpdate"/>
    /// only walks skeletons it already knows.
    /// </summary>
    public void RequestRawTransformRefresh(ISkeleton skeleton)
    {
        GetPoseInfo(skeleton);
        _rawRefreshRequests.Add(SkeletonKey.Of(skeleton));
    }

    /// <summary>The frozen animated/reference baseline beneath the authored
    /// layers; a bone without applied layers has no observation, and its
    /// current transform IS its baseline.</summary>
    public Transform GetAnimatedBaseline(IBone bone) =>
        TryGetEvaluationObservation(bone, out var observation)
            ? observation.AnimatedBaseline
            : bone.LastTransform;

    public bool TryGetEvaluationObservation(
        IBone bone,
        out BoneEvaluationObservation observation)
    {
        if (bone is VirtualBone)
        {
            observation = default;
            return false;
        }

        return _evaluationObservations.TryGetValue(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex),
            out observation);
    }

    /// <summary>Actor teardown: purge every runtime pose store belonging to
    /// an address that no longer hosts a live actor.</summary>
    private void OnActorListChanged(ActorListChangedEvent e)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in e.Actors)
            live.Add(actor.Id.Unique);
        foreach (var key in _poseInfos.Keys.Where(key => !live.Contains(key.Actor)).ToArray())
            PurgeSkeletonState(key);
    }

    /// <summary>Removes one exact skeleton instance's pose state, update
    /// registrations, observations, and IK chain state (a replacement never
    /// inherits configuration or fixed targets).</summary>
    private void PurgeSkeletonState(SkeletonKey key)
    {
        _poseInfos.Remove(key);
        _skeletonsToUpdate.Remove(key);
        _skeletonsToUpdateCache.Remove(key);
        _rawRefreshRequests.Remove(key);
        // A batch registered against a skeleton that is going away can never
        // execute; report it so its owner can roll back instead of waiting.
        if (_transitiveActions.Remove(key, out var orphaned))
            RaiseTransitiveActionsEnded(orphaned);
        RemoveEvaluationObservations(key);
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            _ikChains.Remove(chainKey);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            EndTransitiveActions();
            _poseInfos.Clear();
            _skeletonsToUpdate.Clear();
            _evaluationObservations.Clear();
            _ikChains.Clear();
        }
    }


    /// <summary>Stack deltas are additive for position/scale and multiplicative
    /// for rotation, so identity is (0, identity quaternion, 0).</summary>
    private static bool IsIdentityDelta(Transform delta) =>
        delta.Position == Vector3.Zero &&
        delta.Scale == Vector3.Zero &&
        MathF.Abs(MathF.Abs(delta.Rotation.W) - 1f) < 1e-6f;

    private void ApplyAllBoneTransforms()
    {
        // The pass can purge (and therefore mutate _skeletonsToUpdate) while it
        // runs, so it iterates a snapshot — a REUSED buffer, because this runs
        // in the physics detour every frame and the old ToArray() charged the
        // steady state one array per frame.
        _updatePassBuffer.Clear();
        foreach (var key in _skeletonsToUpdate)
            _updatePassBuffer.Add(key);

        for (var i = 0; i < _updatePassBuffer.Count; i++)
        {
            var slotKey = _updatePassBuffer[i];
            if (!_poseInfos.TryGetValue(slotKey, out var poseInfo))
                continue;

            var actor = FindActor(slotKey.Actor);

            if (actor == null)
            {
                PurgeSkeletonState(slotKey);
                continue;
            }

            // A REPLACED skeleton is not a reason to drop the pose — it is
            // exactly where the pose belongs. The store is keyed by actor and
            // slot, so the apply pass lands the same authored stacks on
            // whatever instance the slot currently holds.
            //
            // A MISSING skeleton is not a reason either: every redraw passes
            // through frames where the actor has no character base, and
            // purging there threw the pose away right before the rebuilt
            // skeleton arrived to receive it. While the ACTOR exists the pose
            // waits; only actor teardown purges.
            var skeleton = _skeletonService.GetSkeleton(actor, slotKey.Slot) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
                continue;

            ApplySkeletonTransforms(slotKey, skeleton, poseInfo);
        }
    }

    /// <summary>
    /// Apply skeleton transforms following Brio's exact pattern:
    /// 1. Apply transforms with per-bone LastTransform update
    /// 2. Full cache update after apply
    /// 3. Reparent partials
    /// 4. Full cache update after reparent
    /// </summary>
    private void ApplySkeletonTransforms(SkeletonKey slotKey, Skeleton skeleton, SkeletonPoseInfo poseInfo)
    {
        // The slot skeleton resolves its OWN native pointer; a weapon or
        // ornament stack is applied through that slot's skeleton only.
        var gameSkeleton = skeleton.GetGameSkeletonPointer();
        if (gameSkeleton == null)
            return;

        // STEP 1: Apply transforms AND update LastTransform per-bone (like Brio ApplyBrioTransforms)
        _transitiveActions.TryGetValue(slotKey, out var actions);
        ApplyTransformsWithPerBoneUpdate(
            slotKey,
            skeleton,
            gameSkeleton,
            poseInfo,
            actions);
        // Brio's pass has no such flag: every skeleton it registers is
        // visited every frame, so a registered action always runs. Poser
        // records the fact so a dropped batch is distinguishable from an
        // executed one at interval end.
        if (actions != null)
            actions.Executed = true;

        // STEP 2: Full cache update after apply (like Brio line 242)
        UpdateAllLastTransforms(skeleton, gameSkeleton);

        // STEP 3: Reparent partials (like Brio line 243)
        ReparentPartials(skeleton, gameSkeleton);

        // STEP 4: Full cache update after reparent (like Brio line 244)
        UpdateAllLastTransforms(skeleton, gameSkeleton);
    }

    /// <summary>
    /// Apply transforms with per-bone LastTransform update - exactly like Brio's ApplyBrioTransforms.
    /// Updates LastTransform IMMEDIATELY after applying each bone's stacks.
    /// </summary>
    private void ApplyTransformsWithPerBoneUpdate(
        SkeletonKey slotKey,
        Skeleton skeleton,
        GameSkeleton* gameSkeleton,
        SkeletonPoseInfo poseInfo,
        TransitiveActionSet? actions)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;

                // The resolved bone's name IS the native name this index would
                // have marshaled (it was resolved BY that name on both paths),
                // so the pose store is keyed identically without the marshal.
                var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, partialIdx);
                _ikChains.TryGetValue(
                    (slotKey, partialIdx, boneIdx), out var chainState);
                bool fixedHold = chainState is
                {
                    Config.Enabled: true,
                    Config.TargetMode: Poser.Domain.Posing.IkTargetMode.Fixed,
                    FixedCapture: not null,
                };
                // Brio visits every bone unconditionally (SkeletonService.cs:98-127)
                // because a transitive action may append a stack to a bone
                // that has none. With no batch registered the pass keeps
                // Poser's cheap skip; with one, every bone is visited.
                if (!bonePoseInfo.HasStacks && !fixedHold && actions == null)
                    continue;

                var baselineSpace = pose->AccessBoneModelSpace(
                    boneIdx,
                    hkaPose.PropagateOrNot.DontPropagate);
                if (baselineSpace == null)
                    continue;
                var animatedBaseline = ReadTransform(baselineSpace);

                // Brio SkeletonService.cs:108 — the stack count taken BEFORE
                // the existing stacks are applied. Everything past it is what
                // the transitive actions appended, and only those are applied
                // a second time below.
                var snapshotCount = bonePoseInfo.Stacks.Count;

                if (bonePoseInfo.HasStacks)
                {
                    // Apply ALL stacks for this bone (like Brio lines 108-112)
                    foreach (var stack in bonePoseInfo.Stacks)
                    {
                        ApplyBoneTransform(pose, boneIdx, stack, bone, chainState);
                    }
                }
                else if (fixedHold)
                {
                    // An armed Fixed chain with no authored stack still holds
                    // its captured target against the running animation.
                    ApplyFixedHold(pose, boneIdx, bone, chainState!);
                }

                // Brio captures both caches immediately after applying each bone.
                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    var transform = ReadTransform(modelSpace);
                    bone.LastRawTransform = transform;
                    bone.LastTransform = transform;
                    if (bonePoseInfo.HasStacks || fixedHold)
                    {
                        _evaluationObservations[
                            (slotKey, partialIdx, boneIdx)] =
                            new BoneEvaluationObservation(
                                _evaluationSequence,
                                animatedBaseline,
                                transform,
                                Combine(bonePoseInfo.Stacks),
                                bonePoseInfo.Stacks.Count);
                    }

                    // Brio SkeletonService.cs:119-127: the actions run against
                    // the caches this pass has just refreshed — the running,
                    // post-parent basis an absolute write must be diffed
                    // against — and whatever they appended is applied here,
                    // in this bone's turn, before the loop moves to its
                    // children.
                    if (actions != null)
                    {
                        ExecuteTransitiveActions(actions, bone, bonePoseInfo);
                        for (var i = snapshotCount; i < bonePoseInfo.Stacks.Count; i++)
                            ApplyBoneTransform(
                                pose, boneIdx, bonePoseInfo.Stacks[i], bone, chainState);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves the SAME managed bone the name path resolves for a native bone,
    /// without marshaling anything while the skeleton's prebuilt map still
    /// describes this pose. <paramref name="map"/> is handed out by
    /// <see cref="Skeleton.GetNativeBoneMap"/> only after the native
    /// <c>hkaSkeleton</c> pointer and bone count have been matched against the
    /// build, so an invalid handle — a rebound partial, a partial the build
    /// never saw, a resized bone array — falls back to marshaling the native
    /// name and asking <see cref="Skeleton.GetBoneByName"/>, exactly as before.
    /// </summary>
    private readonly HashSet<(long, int)> _mapFallbackLogged = new();

    /// <summary>Which skeleton build each slot last seeded a FULL snapshot
    /// for. One full pass per build fills every bone once; after that the
    /// finalize walk copies only bones a reader touched recently.</summary>
    private readonly Dictionary<SkeletonKey, long> _snapshotSeededRevision = new();

    private static Bone? ResolveNativeBone(
        Skeleton skeleton,
        Skeleton.NativeBoneMap map,
        hkaPose* pose,
        int partialIdx,
        int boneIdx)
    {
        if (map.IsValid)
            return map[boneIdx];

        var rawBone = pose->Skeleton->Bones[boneIdx];
        var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";
        return skeleton.GetBoneByName(boneName, partialIdx);
    }

    /// <summary>Refreshes both transform caches at the same two points as Brio:
    /// after applying stacks and after partial reparenting.</summary>
    private void UpdateAllLastTransforms(Skeleton skeleton, GameSkeleton* gameSkeleton)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            // The fallback path resolves EVERY bone BY NAME, allocating a
            // managed copy of the native name per bone per frame — the
            // third-of-a-core in the profile if it is what actually runs.
            // One line per build says which path this partial is on.
            if (!boneMap.IsValid && _mapFallbackLogged.Add(
                    (skeleton.BuildRevision, partialIdx)))
                _log.Debug(
                    $"Bone snapshot FALLBACK for {skeleton.Actor.Name} " +
                    $"{skeleton.Slot} partial {partialIdx} rev " +
                    $"{skeleton.BuildRevision}: the native map failed " +
                    "validation; resolving " +
                    $"{pose->Skeleton->Bones.Length} bones by name each frame.");
            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;

                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    var transform = new Transform
                    {
                        Position = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z),
                        Rotation = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W),
                        Scale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z)
                    };
                    bone.LastRawTransform = transform;
                    bone.LastTransform = transform;
                }
            }
        }
    }

    private void ReparentPartials(Skeleton skeleton, GameSkeleton* gameSkeleton)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;

                if (bone.IsPartialRoot && !bone.IsSkeletonRoot)
                {
                    // Brio performs this access for EVERY partial root and
                    // only afterwards checks whether a parent exists (Brio
                    // SkeletonService.cs:152-153): AccessBoneModelSpace with
                    // Propagate natively syncs the root's model-space entry
                    // and invalidates its descendants, a side effect that
                    // must happen even when no parent transform is written.
                    var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.Propagate);
                    // The Propagate side effect above is the point of the call
                    // and has already happened; the null check only gates the
                    // WRITE below, matching every other model-space deref in
                    // this file (check-then-use, never fail-open).
                    if (modelSpace == null)
                        continue;

                    var parentBone = bone.ParentBone;
                    if (parentBone == null)
                        continue;
                    var parentPartial = &gameSkeleton->PartialSkeletons[parentBone.PartialId];
                    var parentPose = parentPartial->GetHavokPose(0);

                    Vector3 pos;
                    Quaternion rot;
                    Vector3 scale;

                    // A pose that exists but cannot hand back the parent's
                    // model-space entry falls through to the cached transform
                    // arm rather than dereferencing null.
                    var parentModelSpace = parentPose == null
                        ? null
                        : parentPose->AccessBoneModelSpace(parentBone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);

                    if (parentModelSpace != null)
                    {
                        pos = new Vector3(parentModelSpace->Translation.X, parentModelSpace->Translation.Y, parentModelSpace->Translation.Z);
                        rot = new Quaternion(parentModelSpace->Rotation.X, parentModelSpace->Rotation.Y, parentModelSpace->Rotation.Z, parentModelSpace->Rotation.W);
                        scale = new Vector3(parentModelSpace->Scale.X, parentModelSpace->Scale.Y, parentModelSpace->Scale.Z);
                    }
                    else
                    {
                        var parent = parentBone.LastTransform;
                        pos = parent.Position;
                        rot = parent.Rotation;
                        scale = parent.Scale;
                    }

                    // An owned root scale (a duplicate's captured head
                    // scaling) stands in for the parent's.
                    if (bone.PartialRootScale is { } owned)
                        scale = owned;
                    modelSpace->Translation = *(hkVector4f*)(&pos);
                    modelSpace->Rotation = *(hkQuaternionf*)(&rot);
                    modelSpace->Scale = *(hkVector4f*)(&scale);
                }
            }
        }
    }

    /// <summary>Solves the chain toward a Fixed capture when no authored
    /// stack exists: target = captured target + (0 − captured translation).</summary>
    private void ApplyFixedHold(hkaPose* pose, int boneIdx, IBone bone, IkChainState ik)
    {
        var capture = ik.FixedCapture!.Value;
        var target = capture.Target - capture.Translation;
        var rotSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
        var currentRotation = new Quaternion(
            rotSpace->Rotation.X, rotSpace->Rotation.Y,
            rotSpace->Rotation.Z, rotSpace->Rotation.W);
        _ikService.Solve(bone, new Poser.Domain.Posing.IkSolveRequest(
            target, currentRotation, ik.Config, ik.Chain));
        if (!ik.Config.EnforceConstraints)
        {
            var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
            modelSpace->Translation = *(hkVector4f*)(&target);
        }
    }

    private void ApplyBoneTransform(hkaPose* pose, int boneIdx, BonePoseTransformInfo info, IBone bone, IkChainState? ik)
    {
        // Delta mode: ADD to Havok state (like Brio)

        // Ktisis v0.4 action-unit deltas are authored with their axes fixed in
        // the bone's partial-root ("head") frame, not the bone's own frame.
        // Rotation applies pre-multiplied conjugated by the head rotation and
        // the position delta rotates by the head rotation before the model
        // add. Applying them bone-locally is exactly the defect that made
        // Blink open the eyes and Pucker shove the mouth sideways.
        var headRotation = Quaternion.Identity;
        if (info.Frame == TransformFrame.HeadRelative)
        {
            var rootSpace = pose->AccessBoneModelSpace(0, hkaPose.PropagateOrNot.DontPropagate);
            if (rootSpace != null)
                headRotation = new Quaternion(rootSpace->Rotation.X, rootSpace->Rotation.Y, rootSpace->Rotation.Z, rootSpace->Rotation.W);
        }

        // Position
        var prop = info.PropagateComponents.HasFlag(TransformComponents.Position);
        var modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforePos = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        var positionDelta = info.Frame == TransformFrame.HeadRelative
            ? Vector3.Transform(info.Transform.Position, headRotation)
            : info.Transform.Position;
        var tempPos = beforePos + positionDelta;
        bool armed = ik is { Config.Enabled: true };
        bool fixedMode = armed &&
            ik!.Config.TargetMode == Poser.Domain.Posing.IkTargetMode.Fixed;
        bool rotationEnforcedByIk = false;
        if (armed && (fixedMode || info.Transform.Position != Vector3.Zero))
        {
            // Brio-style live IK: the stored delta is the TARGET offset; the
            // chain is solved every frame, so undo/redo stay pure delta
            // operations. Fixed mode targets the captured model-space point
            // shifted by the authored translation moved since capture, so
            // mode changes never jump or double-apply an existing edit.
            var target = tempPos;
            if (fixedMode && ik!.FixedCapture is { } capture)
                target = capture.Target +
                    (info.Transform.Position - capture.Translation);

            // Requested end rotation, computed BEFORE the solve so optional
            // enforcement receives the value the direct apply would produce.
            var rotSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
            var rotBefore = new Quaternion(
                rotSpace->Rotation.X, rotSpace->Rotation.Y,
                rotSpace->Rotation.Z, rotSpace->Rotation.W);
            var requestedRotation = info.Frame == TransformFrame.HeadRelative
                ? Quaternion.Normalize(
                    headRotation * info.Transform.Rotation *
                    Quaternion.Inverse(headRotation) * rotBefore)
                : Quaternion.Normalize(rotBefore * info.Transform.Rotation);

            _ikService.Solve(bone, new Poser.Domain.Posing.IkSolveRequest(
                target, requestedRotation, ik!.Config, ik.Chain));
            // When the solver enforces the end rotation, it is not applied a
            // second time below.
            rotationEnforcedByIk =
                ik.Config.Solver == Poser.Domain.Posing.IkSolver.TwoJoint &&
                ik.Config.EnforceEndRotation;
            if (!ik.Config.EnforceConstraints)
            {
                modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
                modelSpace->Translation = *(hkVector4f*)(&target);
            }
        }
        else
        {
            modelSpace->Translation = *(hkVector4f*)(&tempPos);
        }

        // Rotation (skipped when the Two Joint solver enforced it)
        if (!rotationEnforcedByIk)
        {
            prop = info.PropagateComponents.HasFlag(TransformComponents.Rotation);
            modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
            // A zero basis takes the delta as its whole rotation — the
            // same reading the delta was taken with (BonePoseInfo.UsableBasis).
            var beforeRot = BonePoseInfo.UsableBasis(new Quaternion(
                modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W));
            var tempRot = info.Frame == TransformFrame.HeadRelative
                ? Quaternion.Normalize(
                    headRotation * info.Transform.Rotation *
                    Quaternion.Inverse(headRotation) * beforeRot)
                : Quaternion.Normalize(beforeRot * info.Transform.Rotation);
            modelSpace->Rotation = *(hkQuaternionf*)(&tempRot);
        }

        // Scale
        prop = info.PropagateComponents.HasFlag(TransformComponents.Scale);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeScale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z);
        var tempScale = beforeScale + info.Transform.Scale;
        modelSpace->Scale = *(hkVector4f*)(&tempScale);
    }

    /// <summary>
    /// The store for one (actor, slot), created on first use.
    ///
    /// <para>A plain lookup. It used to be an adoption point — purging stale
    /// instance-keyed stores, taking a parked pose, re-asserting the model
    /// transform — because the key carried the skeleton instance and a redraw
    /// filed the pose under a dead key. The key is names now, so there is
    /// nothing to adopt and nothing to purge: the store the caller gets is the
    /// one the pose was authored into, whichever skeleton is live.</para>
    /// </summary>
    public SkeletonPoseInfo GetPoseInfo(ISkeleton skeleton)
    {
        var slotKey = SkeletonKey.Of(skeleton);
        if (!_poseInfos.TryGetValue(slotKey, out var poseInfo))
        {
            poseInfo = new SkeletonPoseInfo();
            _poseInfos[slotKey] = poseInfo;
        }
        return poseInfo;
    }

    // Default OFF: the eye pair (BoneLinkCatalog j_f_eye_l/r) made a left-eye
    // drag mirror into the right by default (user 2026-08-11: disable it).
    // The Link symmetry mode remains the explicit way to couple edits.
    public bool LinkedBonesEnabled { get; set; }

    private bool _propagatingLinks;

    public void ApplyTransform(IBone bone, Transform newTransform, Transform originalTransform)
    {
        if (bone is VirtualBone)
            return;

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        bonePoseInfo.Apply(newTransform, originalTransform);

        // Linked bones (Anamnesis parity): transfer the SAME delta to the rest
        // of the link set. Re-entrancy guard stops link chains from ping-ponging.
        if (LinkedBonesEnabled && !_propagatingLinks)
        {
            var links = BoneLinkCatalog.GetLinked(bone.BoneName);
            if (links.Count > 0)
            {
                var delta = BonePoseInfo.Diff(newTransform, originalTransform);
                _propagatingLinks = true;
                try
                {
                    foreach (var linkName in links)
                    {
                        var linked = bone.Skeleton.Bones.FirstOrDefault(
                            candidate => candidate.BoneName == linkName &&
                                         candidate.PartialId == bone.PartialId);
                        if (linked == null || linked == bone)
                            continue;

                        var linkedCurrent = linked.LastTransform;
                        var linkedNew = new Transform
                        {
                            Position = linkedCurrent.Position + delta.Position,
                            Rotation = System.Numerics.Quaternion.Normalize(linkedCurrent.Rotation * delta.Rotation),
                            Scale = linkedCurrent.Scale + delta.Scale,
                        };
                        ApplyTransform(linked, linkedNew, linkedCurrent);
                    }
                }
                finally
                {
                    _propagatingLinks = false;
                }
            }
        }

    }

    /// <summary>Brio's <c>EligibleForIK</c> — a parent for the solver to walk
    /// into, and not a hidden one (<c>Brio/Game/Posing/Skeletons/Bone.cs:68</c>).
    /// A bone that heads no declared chain is armable on this rule alone,
    /// because CCD needs nothing but the parent walk.</summary>
    private static bool IsCcdEligible(IBone bone) =>
        bone is not VirtualBone &&
        bone.ParentBone is { IsHiddenBone: false };

    public Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone)
    {
        if (bone is VirtualBone)
            return null;
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null && !IsCcdEligible(bone))
            return null;
        var key = ChainKey(bone);
        if (_ikChains.TryGetValue(key, out var state))
            return state.Config;
        return definition == null
            ? Poser.Domain.Posing.IkChainConfig.DefaultsForChain()
            : Poser.Domain.Posing.IkChainConfig.DefaultsFor(definition.IsArm);
    }

    public IReadOnlyList<Poser.Services.IkConfiguredChain> GetIkChains(
        ISkeleton skeleton)
    {
        var key = SkeletonKey.Of(skeleton);
        List<Poser.Services.IkConfiguredChain>? chains = null;
        foreach (var (chainKey, state) in _ikChains)
        {
            if (chainKey.Skeleton != key)
                continue;
            var endpoint = (skeleton as Skeleton)?
                .GetBone(chainKey.Partial, chainKey.Bone);
            if (endpoint == null)
                continue;
            (chains ??= new()).Add(new Poser.Services.IkConfiguredChain(
                endpoint,
                state.Config,
                ChainMemberNames(endpoint, state.Config)));
        }
        return (IReadOnlyList<Poser.Services.IkConfiguredChain>?)chains
            ?? Array.Empty<Poser.Services.IkConfiguredChain>();
    }

    /// <summary>Which bones the configured solver actually moves. CCD walks
    /// the endpoint's own parents to the configured depth — the same walk
    /// IKService.GetBonesToDepth and IkBakeCapture.AffectedBones make, because
    /// the chain is not declared anywhere to read it from.</summary>
    private static IReadOnlyList<string> ChainMemberNames(
        IBone endpoint,
        Poser.Domain.Posing.IkChainConfig config)
    {
        var names = new List<string> { endpoint.BoneName };
        if (config.Solver != Poser.Domain.Posing.IkSolver.TwoJoint)
        {
            var current = endpoint.ParentBone;
            while (current != null && names.Count < config.CcdDepth + 1)
            {
                names.Add(current.BoneName);
                current = current.ParentBone;
            }
            return names;
        }

        if (Poser.Domain.Posing.IkChains.ForEndpoint(endpoint.BoneName)
            is not { } definition)
            return names;
        names.Add(definition.Endpoint);
        names.Add(definition.FirstJoint);
        names.Add(definition.SecondJoint);
        if (definition.FirstTwist != null)
            names.Add(definition.FirstTwist);
        if (definition.SecondTwist != null)
            names.Add(definition.SecondTwist);
        return names;
    }

    public string? SetIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config)
    {
        if (bone is VirtualBone)
            return "Virtual bones cannot use IK.";
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null)
        {
            if (!IsCcdEligible(bone))
                return $"{bone.BoneName} has no parent for IK to bend.";
            if (config.ValidateUndeclared() is { } rejected)
                return rejected;
            return StoreIkConfiguration(
                bone,
                config,
                // CCD reads only the endpoint; the joint slots stay unresolved
                // so nothing can mistake this for a Two Joint chain.
                new Poser.Domain.Posing.IkResolvedChain(
                    -1, -1, -1, -1, (short)bone.BoneIndex));
        }
        if (config.Validate() is { } invalid)
            return invalid;
        var chain = ResolveChain(bone, definition);
        if (config.Solver == Poser.Domain.Posing.IkSolver.TwoJoint &&
            !chain.TwoJointAvailable)
            return "The Two Joint chain does not resolve on this skeleton.";
        return StoreIkConfiguration(bone, config, chain);
    }

    private string? StoreIkConfiguration(
        IBone bone,
        Poser.Domain.Posing.IkChainConfig config,
        Poser.Domain.Posing.IkResolvedChain chain)
    {
        var key = ChainKey(bone);
        _ikChains.TryGetValue(key, out var previous);
        var state = previous ?? new IkChainState { Config = config };
        state.Config = config.Normalized();
        state.Chain = chain;

        // Fixed-target lifecycle: capture on entering Fixed or enabling a
        // Fixed chain; disabling retains tuning but clears the capture.
        if (!config.Enabled)
        {
            state.FixedCapture = null;
        }
        else if (config.TargetMode == Poser.Domain.Posing.IkTargetMode.Fixed &&
                 (previous == null ||
                  previous.Config.TargetMode != Poser.Domain.Posing.IkTargetMode.Fixed ||
                  !previous.Config.Enabled ||
                  state.FixedCapture == null))
        {
            state.FixedCapture = (
                bone.LastTransform.Position,
                GetModification(bone)?.Position ?? Vector3.Zero);
        }
        else if (config.TargetMode == Poser.Domain.Posing.IkTargetMode.Relative)
        {
            state.FixedCapture = null;
        }

        _ikChains[key] = state;
        // Materialize the pose-info entry so the per-frame update loop
        // visits this skeleton even before any stack exists.
        GetPoseInfo(bone.Skeleton);
        return null;
    }

    public bool IsIkTwoJointAvailable(IBone bone)
    {
        if (bone is VirtualBone)
            return false;
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        return definition != null && ResolveChain(bone, definition).TwoJointAvailable;
    }

    public void ClearIkConfigurations(ISkeleton skeleton)
    {
        var key = SkeletonKey.Of(skeleton);
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            _ikChains.Remove(chainKey);

    }

    private bool HasEnabledChains(SkeletonKey key)
    {
        foreach (var (chainKey, state) in _ikChains)
        {
            if (chainKey.Skeleton == key && state.Config.Enabled)
                return true;
        }
        return false;
    }

    private static (SkeletonKey Skeleton, int Partial, int Bone) ChainKey(IBone bone) =>
        (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex);

    /// <summary>Resolves the chain inside the endpoint's OWN skeleton and
    /// partial; missing optional twists resolve to native index -1 and a
    /// missing mandatory joint makes Two Joint unavailable.</summary>
    private static Poser.Domain.Posing.IkResolvedChain ResolveChain(
        IBone endpoint,
        Poser.Domain.Posing.IkChainDefinition definition)
    {
        short Index(string? name)
        {
            if (name == null)
                return -1;
            var resolved = (endpoint.Skeleton as Skeleton)?
                .GetBoneByName(name, endpoint.PartialId);
            return resolved == null ? (short)-1 : (short)resolved.BoneIndex;
        }

        return new Poser.Domain.Posing.IkResolvedChain(
            Index(definition.FirstJoint),
            Index(definition.FirstTwist),
            Index(definition.SecondJoint),
            Index(definition.SecondTwist),
            (short)endpoint.BoneIndex);
    }

    public bool HasEnabledIk(ISkeleton skeleton) =>
        HasEnabledChains(SkeletonKey.Of(skeleton));

    public void ResetBone(IBone bone)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        bonePoseInfo.ClearStacks();
        _evaluationObservations.Remove(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex));

    }

    public void ResetSkeleton(ISkeleton skeleton)
    {
        var slotKey = SkeletonKey.Of(skeleton);
        if (_poseInfos.TryGetValue(slotKey, out var poseInfo))
        {
            poseInfo.Clear();
        }
        RemoveEvaluationObservations(slotKey);
    }

    public bool HasModifications(IBone bone)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var poseInfo))
            return false;

        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        return bonePoseInfo.HasStacks;
    }

    public Transform? GetModification(IBone bone)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var poseInfo))
            return null;

        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        if (!bonePoseInfo.HasStacks)
            return null;

        var combined = Transform.Zero;
        foreach (var stack in bonePoseInfo.Stacks)
        {
            combined = new Transform
            {
                Position = combined.Position + stack.Transform.Position,
                Rotation = Quaternion.Normalize(combined.Rotation * stack.Transform.Rotation),
                Scale = combined.Scale + stack.Transform.Scale
            };
        }
        return combined;
    }

    public IReadOnlyList<BonePoseTransformInfo> CapturePoseStacks(IBone bone)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var poseInfo))
            return Array.Empty<BonePoseTransformInfo>();

        return poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId).Stacks.ToArray();
    }

    public void RestorePoseStacks(IBone bone, IReadOnlyList<BonePoseTransformInfo> stacks)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        bonePoseInfo.RestoreInteractiveStacks(stacks);
    }

    /// <summary>
    /// FinalizeSkeletonsDetour - matches Brio's FinalizeSkeletonUpdate exactly.
    /// STEP 5: Final update for ALL modified skeletons after engine is done.
    /// </summary>
    private void FinalizeSkeletonsDetour(nint a1)
    {
        _finalizeSkeletonsHook!.Original(a1);

        // Never fault the native render frame (CharacterFinalizeDetour
        // standard): everything after Original is managed bookkeeping.
        try
        {
            FinalizeSkeletons();
        }
        catch (Exception ex)
        {
            if (!_finalizeDetourFaultLogged)
            {
                _finalizeDetourFaultLogged = true;
                _log.Error($"BonePosingService: finalize pass faulted (logged once): {ex}");
            }
        }
    }

    private void FinalizeSkeletons()
    {
        if (!_gPoseService.IsGPosing)
        {
            // Brio's interval does not end outside gpose either, but a batch
            // registered on the way out would then wait forever; end it as
            // not executed.
            EndTransitiveActions();
            return;
        }

        // The snapshot exists FOR its readers: the overlay, the inspector,
        // the matrix. No fresh reader, no walk — a hidden UI stops paying a
        // third of a core for transforms nobody looks at.
        if (!BoneSnapshotDemand.Wanted())
            return;

        // STEP 5: Final update for ALL modified skeletons (like Brio line 263)
        // This takes a final snapshot now the engine is done touching skeletons.
        // Both sets are snapshotted FIRST: UpdateSkeletonCache → GetSkeleton
        // publishes SkeletonChangedEvent synchronously when a slot vanished or
        // was replaced, and OnSkeletonChanged mutates both live sets — the
        // same mutation-during-enumeration hazard ApplyAllBoneTransforms
        // already snapshots against.
        _finalizePassBuffer.Clear();
        foreach (var key in _skeletonsToUpdate)
            _finalizePassBuffer.Add(key);
        _finalizeCachePassBuffer.Clear();
        foreach (var key in _skeletonsToUpdateCache)
            _finalizeCachePassBuffer.Add(key);

        for (var i = 0; i < _finalizePassBuffer.Count; i++)
        {
            UpdateSkeletonCache(_finalizePassBuffer[i]);
        }

        // Also update overlay-only skeletons that don't have modifications.
        // Dedupe against the SNAPSHOT, not the live set: an entry the event
        // handler re-adds during the first loop was not updated by it.
        for (var i = 0; i < _finalizeCachePassBuffer.Count; i++)
        {
            var slotKey = _finalizeCachePassBuffer[i];
            if (!_finalizePassBuffer.Contains(slotKey))
            {
                UpdateSkeletonCache(slotKey);
            }
        }

        // Brio SkeletonService.cs:266 — the posing interval ends here, and
        // with it every registered transitive action.
        EndTransitiveActions();
    }

    /// <summary>Indexed scan, not foreach: <c>Actors</c> is an interface-typed
    /// list, so foreach boxes an enumerator on every call and these callers run
    /// per posed skeleton per frame inside the detours.</summary>
    private IActor? FindActor(string actorId)
    {
        var actors = _actorManager.Actors;
        for (var i = 0; i < actors.Count; i++)
        {
            if (actors[i].Id.Unique == actorId)
                return actors[i];
        }
        // The CharaView preview body poses through the same apply pass; a miss
        // here purges its pose state on the very next frame.
        var auxiliary = _actorManager.AuxiliaryActors;
        for (var i = 0; i < auxiliary.Count; i++)
        {
            if (auxiliary[i].Id.Unique == actorId)
                return auxiliary[i];
        }
        return null;
    }

    private void UpdateSkeletonCache(SkeletonKey slotKey)
    {
        var actor = FindActor(slotKey.Actor);
        if (actor == null)
            return;

        var skeleton = _skeletonService.GetSkeleton(actor, slotKey.Slot) as Skeleton;
        if (skeleton == null || !skeleton.IsValid)
            return;

        var gameSkeleton = skeleton.GetGameSkeletonPointer();
        if (gameSkeleton == null)
            return;

        // The walk is pull-driven: only bones whose transform something READ
        // in the last couple of frames are copied — an overlay mask shows
        // dozens of a skeleton's hundreds. A new build seeds one full pass.
        bool seedAll = !_snapshotSeededRevision.TryGetValue(slotKey, out var seededRev)
            || seededRev != skeleton.BuildRevision;
        if (seedAll)
            _snapshotSeededRevision[slotKey] = skeleton.BuildRevision;

        for (int partialIdx = 0; partialIdx < gameSkeleton->PartialSkeletonCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            // The fallback resolves EVERY bone BY NAME, allocating a managed
            // copy of the native name per bone per frame — the
            // third-of-a-core in the profile if it is what actually runs.
            if (!boneMap.IsValid && _mapFallbackLogged.Add(
                    (skeleton.BuildRevision, partialIdx)))
                _log.Debug(
                    $"Bone snapshot FALLBACK for {skeleton.Actor.Name} " +
                    $"{skeleton.Slot} partial {partialIdx} rev " +
                    $"{skeleton.BuildRevision}: the native map failed " +
                    "validation; resolving " +
                    $"{pose->Skeleton->Bones.Length} bones by name each frame.");
            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;
                if (!seedAll && !bone.TransformWanted)
                    continue;

                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    bone.LastTransform = new Transform
                    {
                        Position = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z),
                        Rotation = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W),
                        Scale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z)
                    };
                }
            }
        }
    }

    public void FlipBone(IBone bone)
    {
        if (bone is VirtualBone)
            return;

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        // Get current rotation and convert to euler
        var currentRotation = bone.LastTransform.Rotation;
        var euler = QuaternionToEuler(currentRotation);

        // Flip: X = 180 - X, Y = -Y (matching Brio's approach)
        euler.X = 180f - euler.X;
        euler.Y = -euler.Y;

        var newRotation = EulerToQuaternion(euler);

        // Create new transform with flipped rotation
        var newTransform = new Transform
        {
            Position = bone.LastTransform.Position,
            Rotation = newRotation,
            Scale = bone.LastTransform.Scale
        };

        // LastRawTransform is the posed value (anim ⊕ existing stacks), so the
        // diff is only valid on top of those stacks — they must survive, like
        // Brio's PosingCapability.FlipBone which accumulates and never clears.
        bonePoseInfo.Apply(newTransform, bone.LastRawTransform);

    }

    public string? GetMirrorBoneName(string boneName) => PoseMath.GetMirrorBoneName(boneName);

    private static Vector3 QuaternionToEuler(Quaternion r) => PoseMath.QuaternionToEuler(r);

    private static Quaternion EulerToQuaternion(Vector3 euler) => PoseMath.EulerToQuaternion(euler);

    private static Transform ReadTransform(hkQsTransformf* transform) =>
        new()
        {
            Position = new Vector3(
                transform->Translation.X,
                transform->Translation.Y,
                transform->Translation.Z),
            Rotation = new Quaternion(
                transform->Rotation.X,
                transform->Rotation.Y,
                transform->Rotation.Z,
                transform->Rotation.W),
            Scale = new Vector3(
                transform->Scale.X,
                transform->Scale.Y,
                transform->Scale.Z),
        };

    private static Transform Combine(
        IReadOnlyList<BonePoseTransformInfo> stacks)
    {
        var combined = Transform.Zero;
        foreach (var stack in stacks)
        {
            combined = new Transform
            {
                Position = combined.Position + stack.Transform.Position,
                Rotation = Quaternion.Normalize(
                    combined.Rotation * stack.Transform.Rotation),
                Scale = combined.Scale + stack.Transform.Scale,
            };
        }
        return combined;
    }

    /// <summary>Runs every frame for every registered-but-unposed skeleton
    /// (OnFrameworkUpdate), so it collects into a reused buffer instead of the
    /// LINQ chain + array it used to allocate per skeleton per frame.</summary>
    private void RemoveEvaluationObservations(SkeletonKey slotKey)
    {
        if (_evaluationObservations.Count == 0)
            return;

        _observationRemovalBuffer.Clear();
        foreach (var key in _evaluationObservations.Keys)
        {
            if (key.Skeleton == slotKey)
                _observationRemovalBuffer.Add(key);
        }

        for (var i = 0; i < _observationRemovalBuffer.Count; i++)
            _evaluationObservations.Remove(_observationRemovalBuffer[i]);
    }

    public void Dispose()
    {
        _updateBonePhysicsHook?.Dispose();
        _finalizeSkeletonsHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        EndTransitiveActions();
        _poseInfos.Clear();
        _evaluationObservations.Clear();
        GC.SuppressFinalize(this);
    }
}
