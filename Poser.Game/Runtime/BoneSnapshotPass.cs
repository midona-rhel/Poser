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
/// The native finalize snapshot: the FinalizeSkeletons hook, which copies
/// the engine's final bone transforms into the caches readers use — only for
/// skeletons the apply pass visited or a reader registered, and only bones a
/// reader touched recently once a build has been seeded — and ends the
/// posing interval's transitive batches.
/// </summary>
internal sealed unsafe class BoneSnapshotPass : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IActorManager _actorManager;
    private readonly BoneApplyPass _apply;
    private readonly TransitiveActionScheduler _transitive;

    // Hook for finalizing skeletons before rendering (takes final snapshot)
    private delegate void FinalizeSkeletonsDelegate(nint a1);
    private Hook<FinalizeSkeletonsDelegate>? _finalizeSkeletonsHook;

    // Track which slot skeletons need cache updates (visible overlays, active gizmo, etc.)
    private readonly HashSet<SkeletonKey> _skeletonsToUpdateCache = new();

    /// <summary>Reused snapshot buffers for the finalize pass — same hazard,
    /// same idiom as the apply pass's: the pass may purge or re-add entries
    /// in BOTH live sets, which would throw mid-enumeration inside the
    /// FinalizeSkeletons native frame. (Skeleton changes found here are
    /// published on the next framework update, never in this frame.)
    /// Single-threaded (render detour), never nested.</summary>
    private readonly List<SkeletonKey> _finalizePassBuffer = new();
    private readonly List<SkeletonKey> _finalizeCachePassBuffer = new();

    // One-shot fault flag: the detour runs every frame, so a repeating
    // fault must not turn the log into a firehose.
    private bool _finalizeDetourFaultLogged;

    /// <summary>Which skeleton build each slot last seeded a FULL snapshot
    /// for. One full pass per build fills every bone once; after that the
    /// finalize walk copies only bones a reader touched recently.</summary>
    private readonly Dictionary<SkeletonKey, long> _snapshotSeededRevision = new();

    public BoneSnapshotPass(
        IPluginLog log,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IActorManager actorManager,
        BoneApplyPass apply,
        TransitiveActionScheduler transitive)
    {
        _log = log;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _actorManager = actorManager;
        _apply = apply;
        _transitive = transitive;
    }

    /// <summary>Installs and enables the FinalizeSkeletons hook. Called once
    /// the owner has built everything the detour reads.</summary>
    public void InstallHook(IGameInteropProvider hooking, ISigScanner scanner)
    {
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
    }

    public void Register(SkeletonKey key) => _skeletonsToUpdateCache.Add(key);

    public void Unregister(SkeletonKey key) => _skeletonsToUpdateCache.Remove(key);

    public void ClearCacheRequests() => _skeletonsToUpdateCache.Clear();

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
            _transitive.EndTransitiveActions();
            return;
        }

        // The snapshot exists FOR its readers: the overlay, the inspector,
        // the matrix. No fresh reader, no walk — a hidden UI stops paying a
        // third of a core for transforms nobody looks at.
        if (!BoneSnapshotDemand.Wanted())
            return;

        // STEP 5: Final update for ALL modified skeletons (like Brio line 263)
        // This takes a final snapshot now the engine is done touching skeletons.
        // Both sets are snapshotted FIRST: the pass can mutate both live
        // sets — the same mutation-during-enumeration hazard
        // ApplyAllBoneTransforms already snapshots against.
        _finalizePassBuffer.Clear();
        foreach (var key in _apply.UpdateSet)
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
        _transitive.EndTransitiveActions();
    }

    private void UpdateSkeletonCache(SkeletonKey slotKey)
    {
        var actor = BoneApplyPass.FindActor(_actorManager, slotKey.Actor);
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
            if (!boneMap.IsValid && _apply.FirstFallback(
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
                var bone = BoneApplyPass.ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
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

    public void Dispose() => _finalizeSkeletonsHook?.Dispose();
}
