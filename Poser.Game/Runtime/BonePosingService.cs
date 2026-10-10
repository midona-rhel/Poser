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
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Identity;
using Poser.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Service for manipulating bone transforms using game hooks.
/// Simple delta-based system like Brio - bones rotate around themselves.
///
/// <para>The facade over the posing engine's owners: <see cref="PoseStackStore"/>
/// (authored stacks and apply space), <see cref="IkChainRegistry"/> (session
/// IK), <see cref="TransitiveActionScheduler"/> (in-pass batches), and the two
/// native passes, <see cref="BoneApplyPass"/> and <see cref="BoneSnapshotPass"/>.
/// It keeps the authored-edit entry point, linked bones, and the actor and
/// GPose lifecycle that purges them all together.</para>
/// </summary>
public unsafe class BonePosingService : IBonePosingService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IEventBus _eventBus;
    private readonly Poser.Config.ConfigurationService _configuration;

    private readonly PoseStackStore _stacks = new();
    private readonly TransitiveActionScheduler _transitive;
    private readonly IkChainRegistry _ik;
    private readonly BoneApplyPass _apply;
    private readonly BoneSnapshotPass _snapshot;

    public BonePosingService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IActorManager actorManager,
        IEventBus eventBus,
        IKService ikService,
        Poser.Game.Bindings.StableBindingRegistry bindings,
        IPosingService posingService,
        Poser.Config.ConfigurationService configuration,
        IGameInteropProvider hooking,
        ISigScanner scanner,
        Poser.Game.Posing.GazePoseFrames gazeFrames)
    {
        _log = log;
        _framework = framework;
        _eventBus = eventBus;
        _configuration = configuration;
        _transitive = new TransitiveActionScheduler(log);
        _ik = new IkChainRegistry(
            _stacks, ikService, bindings, skeletonService, new IkHeldTargets(_stacks, bindings));

        _apply = new BoneApplyPass(
            log, gPoseService, skeletonService, actorManager, ikService, gazeFrames,
            _stacks, _ik, _transitive, PurgeSkeletonState);
        _snapshot = new BoneSnapshotPass(
            log, gPoseService, skeletonService, actorManager, _apply, _transitive);

        // The two native hooks, in their original order: UpdateBonePhysics
        // (the apply pass), then FinalizeSkeletons (the snapshot). Both are
        // enabled only once everything either detour reads exists.
        _apply.InstallHook(hooking, scanner);
        _snapshot.InstallHook(hooking, scanner);

        _framework.Update += OnFrameworkUpdate;
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);

        _log.Debug("BonePosingService initialized");
    }

    public void SetIkImportSuppressed(string actorKey, bool suppressed) =>
        _ik.SetIkImportSuppressed(actorKey, suppressed);

    private void OnFrameworkUpdate(IFramework framework)
    {
        _apply.Rebuild();
        _snapshot.ClearCacheRequests();
    }

    /// <summary>
    /// Brio's <c>SkeletonPosingCapability.RegisterTransitiveAction</c>
    /// (SkeletonPosingCapability.cs:52-55). The action runs once for every
    /// bone of this slot skeleton, inside the physics-detour apply pass, at
    /// the point where the bone's existing stacks have been applied and its
    /// transform caches refreshed.
    /// </summary>
    public void RegisterTransitiveAction(
        ISkeleton skeleton,
        Action<IBone, BonePoseInfo> action)
    {
        _transitive.Register(skeleton, action);

        // Materialize the pose store so the per-frame rebuild can see the
        // skeleton at all, and register directly for the pass that is still
        // ahead of us this frame (registration from a framework update
        // precedes this frame's detour; registration from UI draw follows it
        // and is picked up by the next rebuild).
        _stacks.GetPoseInfo(skeleton);
        _apply.Schedule(SkeletonKey.Of(skeleton));
    }

    public event Action<TransitiveActionOutcome>? TransitiveActionsEnded
    {
        add => _transitive.TransitiveActionsEnded += value;
        remove => _transitive.TransitiveActionsEnded -= value;
    }

    public void RegisterSkeletonForCacheUpdate(ISkeleton skeleton) =>
        _snapshot.Register(SkeletonKey.Of(skeleton));

    /// <summary>
    /// One frame's membership in the apply pass, for a caller that needs
    /// <see cref="IBone.LastRawTransform"/> refreshed on a skeleton that
    /// carries nothing the pass would otherwise select it for. The pose store
    /// is materialized because the per-frame rebuild only walks skeletons it
    /// already knows.
    /// </summary>
    public void RequestRawTransformRefresh(ISkeleton skeleton)
    {
        _stacks.GetPoseInfo(skeleton);
        _apply.RequestRawRefresh(SkeletonKey.Of(skeleton));
    }

    /// <summary>The frozen animated/reference baseline beneath the authored
    /// layers; a bone without applied layers has no captured baseline, and its
    /// current transform IS its baseline.</summary>
    public Transform GetAnimatedBaseline(IBone bone) => _stacks.GetAnimatedBaseline(bone);

    /// <summary>Actor teardown: purge every runtime pose store belonging to
    /// an address that no longer hosts a live actor.</summary>
    private void OnActorListChanged(ActorListChangedEvent e)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in e.Actors)
            live.Add(actor.Id.Unique);
        foreach (var key in _stacks.Keys.Where(key => !live.Contains(key.Actor)).ToArray())
            PurgeSkeletonState(key);
    }

    /// <summary>Removes one exact skeleton instance's pose state, update
    /// registrations, observations, and IK chain state (a replacement never
    /// inherits configuration or fixed targets).</summary>
    private void PurgeSkeletonState(SkeletonKey key)
    {
        _stacks.RemovePartialFrames(key);
        _stacks.RemovePoseInfo(key);
        _apply.Unschedule(key);
        _snapshot.Unregister(key);
        // A batch registered against a skeleton that is going away can never
        // execute; report it so its owner can roll back instead of waiting.
        _transitive.Orphan(key);
        _stacks.RemoveAnimatedBaselines(key);
        _ik.Purge(key);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            _log.Information($"[GPoseLifetime] ik-cleanup-before chains={_ik.Count} poseSlots={_stacks.Count}");
            _transitive.EndTransitiveActions();
            _stacks.Clear();
            _apply.ClearUpdateSet();
            _ik.ClearAll();
            _log.Information("[GPoseLifetime] ik-cleanup-complete chains=0");
        }
    }

    public SkeletonPoseInfo GetPoseInfo(ISkeleton skeleton) => _stacks.GetPoseInfo(skeleton);

    // Default OFF: the eye pair (BoneLinkCatalog j_f_eye_l/r) made a left-eye
    // drag mirror into the right by default (user 2026-08-11: disable it).
    // The Link symmetry mode remains the explicit way to couple edits.
    public bool LinkedBonesSuppressed { get; set; }
    public bool LinkedBonesEnabled => !LinkedBonesSuppressed && (_configuration?.Config.LinkSiblingBones ?? false);

    private bool _propagatingLinks;

    public Transform ToApplySpace(IBone bone, Transform visible) => _stacks.ToApplySpace(bone, visible);

    public void ApplyTransform(IBone bone, Transform newTransform, Transform originalTransform)
    {
        if (bone is VirtualBone)
            return;

        newTransform = newTransform with { Position = originalTransform.Position
            + ClampIkTranslation(bone, newTransform.Position - originalTransform.Position, fromAuthoredBaseline: true) };

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        var applied = ToApplySpace(bone, newTransform);
        var original = ToApplySpace(bone, originalTransform);
        if (!_ik.IsImportSuppressed(SkeletonKey.Of(bone.Skeleton).Actor)
            && GetIkConfiguration(bone) is { Enabled: true, TargetMode: IkTargetMode.Actor, ActorAnchor: { } anchor })
        {
            // Keep existing handle edits attached to the external parent, but
            // interpret each new drag in the current model axes, not old axes.
            applied.Position = original.Position + anchor.ToReferenceDelta(
                applied.Position - original.Position, _ik.Held.ActorParentFrame(bone, anchor));
        }
        bonePoseInfo.Apply(applied, original);

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

    // ── IK ───────────────────────────────────────────────────────────────

    public Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone) =>
        _ik.GetIkConfiguration(bone);

    public IReadOnlyList<Poser.Services.IkConfiguredChain> GetIkChains(ISkeleton skeleton) =>
        _ik.GetIkChains(skeleton);

    public string? SetIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config) =>
        _ik.SetIkConfiguration(bone, config);

    public IkChainConfig PrepareIkConfiguration(IBone endpoint, IkChainConfig config) =>
        _ik.PrepareIkConfiguration(endpoint, config);

    public FabrikTarget? CaptureFabrikTarget(IBone endpoint, IkTargetMode mode,
        BoneId? bone = null, SelectionId? entity = null) =>
        _ik.CaptureFabrikTarget(endpoint, mode, bone, entity);

    public IkChainConfig? SnapshotFabrik(IBone tip, bool modelSpace = false) =>
        _ik.SnapshotFabrik(tip, modelSpace);

    public string? RestoreFabrik(IBone tip, IkChainConfig config) =>
        _ik.RestoreFabrik(tip, config);

    public Vector3 ClampIkTranslation(IBone bone, Vector3 delta, bool fromAuthoredBaseline = false) =>
        _ik.ClampIkTranslation(bone, delta, fromAuthoredBaseline);

    public string? SetIkBoneTarget(IBone endpoint, IBone target) =>
        _ik.SetIkBoneTarget(endpoint, target);

    public IBone? GetIkBoneTarget(IBone endpoint) => _ik.GetIkBoneTarget(endpoint);

    public string? SetIkEntityTarget(IBone endpoint, SelectionId target) =>
        _ik.SetIkEntityTarget(endpoint, target);

    public SelectionId? GetIkEntityTarget(IBone endpoint) => _ik.GetIkEntityTarget(endpoint);

    /// <summary>Snapshot enabled constraints into the preview's own model frame.
    /// No native bones or live scene targets are retained by the copied state.</summary>
    public void CopyPreviewIk(IActor? source, IActor preview) => _ik.CopyPreviewIk(source, preview);

    public bool IsIkTwoJointAvailable(IBone bone) => _ik.IsIkTwoJointAvailable(bone);

    public void ClearIkConfigurations(ISkeleton skeleton) => _ik.ClearIkConfigurations(skeleton);

    public bool HasEnabledIk(ISkeleton skeleton) => _ik.HasEnabledIk(skeleton);

    // ── stacks ───────────────────────────────────────────────────────────

    public void ResetBone(IBone bone) => _stacks.ResetBone(bone);

    public void ResetSkeleton(ISkeleton skeleton) => _stacks.ResetSkeleton(skeleton);

    public bool HasModifications(IBone bone) => _stacks.HasModifications(bone);

    public Transform? GetModification(IBone bone) => _stacks.GetModification(bone);

    public IReadOnlyList<BonePoseTransformInfo> CapturePoseStacks(IBone bone) =>
        _stacks.CapturePoseStacks(bone);

    public void RestorePoseStacks(IBone bone, IReadOnlyList<BonePoseTransformInfo> stacks) =>
        _stacks.RestorePoseStacks(bone, stacks);

    public void FlipBone(IBone bone) => _stacks.FlipBone(bone);

    public string? GetMirrorBoneName(string boneName) => PoseMath.GetMirrorBoneName(boneName);

    public void Dispose()
    {
        _ik.DisposeChains();
        _apply.Dispose();
        _snapshot.Dispose();
        _framework.Update -= OnFrameworkUpdate;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        _transitive.EndTransitiveActions();
        _stacks.ClearStoresAndBaselines();
        GC.SuppressFinalize(this);
    }
}
