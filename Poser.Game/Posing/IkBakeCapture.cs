using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Turns a live-solved IK chain into ordinary pose edits (Brio's "Set IK
/// Changes" / <c>SkeletonPosingCapability.ResetIK</c>).
///
/// Poser's IK is live: the chain is re-solved every frame inside the native
/// skeleton update and the result exists only in the native pose, in no
/// stack. Disarming therefore abandons the solved placement and the limb
/// snaps back to whatever the authored stacks alone produce.
///
/// The bake does not compute a delta of its own. It reuses, verbatim, the one
/// mechanism already proven to turn a target pose into stacks that reproduce
/// it — the pose-file import — exactly as Brio's ResetIK does:
///
///   1. Snapshot the ENTIRE skeleton set with
///      <see cref="IPoseFileService.CreatePoseFile"/>. These are the same
///      absolutes a .pose export writes, taken while the chain is still
///      armed, so they include the solved limb.
///   2. Disarm the chain. Nothing else changes: every authored stack stays
///      exactly where it was.
///   3. Let ONE apply pass run with the chain disarmed, then replay the
///      snapshot through <see cref="CleanPoseFacade.ImportPose(IActor,
///      PoseFile, PoseImportOptions, string)"/> — the same plan builder, the
///      same conversion and the same atomic <c>ImportEdit</c> a user's .pose
///      apply uses, with the default options (all components, whole skeleton,
///      no reset-before-import, no model transform).
///
/// Step 3's wait is the whole reason this works and is not optional. An
/// import write is <c>BonePoseInfo.Apply(desired, bone.LastRawTransform)</c>:
/// it ACCUMULATES onto the bone's existing stack a delta measured from the
/// bone's last cached model-space value. That cancels to the correct absolute
/// only while <c>LastRawTransform == animatedBaseline ⊕ stacks</c> — the
/// invariant the apply pass re-establishes every frame. A solved chain breaks
/// it, because the solver's contribution is in <c>LastRawTransform</c> and in
/// no stack: importing on the disarm tick diffs the snapshot against itself,
/// yields identity deltas, and drops the limb. One pass with the chain
/// disarmed restores the invariant, and from there the import behaves exactly
/// as it does on a settled skeleton.
///
/// Whole skeleton, not the chain subset — Brio bakes the whole skeleton too.
/// Every other bone's snapshot value equals its own basis, so its write is an
/// identity delta and its stack comes out unchanged; there is no subset or
/// propagation question left to get wrong.
/// </summary>
public sealed class IkBakeCapture : IDisposable
{
    /// <summary>Framework ticks between arming the bake and applying it. Two,
    /// not one, because <see cref="Begin"/> is called both from UI draw (after
    /// this tick's apply pass) and from the framework update (before it); two
    /// ticks guarantee a full disarmed pass either way.</summary>
    private const int ApplyDelayTicks = 2;

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly CleanPoseFacade _poses;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    private sealed record Pending(
        TransformTargetId Target,
        IActor Actor,
        IBone Endpoint,
        IkChainConfig Config,
        PoseFile Snapshot);

    private Pending? _pending;

    public IkBakeCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        IBonePosingService posing,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        CleanPoseFacade poses,
        TransformGestureService gestures,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _posing = posing;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _poses = poses;
        _gestures = gestures;
        _log = log;
    }

    /// <summary>The bake's own status line: "in flight" while the disarmed
    /// pass elapses, then the reason if the deferred apply failed, cleared on
    /// success. Carries its target so it cannot survive onto another bone's IK
    /// section.</summary>
    public (TransformTargetId Target, string Text)? Note { get; private set; }

    /// <summary>Whether a bake is armed and waiting for its apply tick.</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Whether this endpoint has a solve worth baking: armed, its chain
    /// resolves, and the runtime's own solve condition holds (a Fixed chain
    /// always holds a captured target; a Relative one needs an authored
    /// translation).
    /// </summary>
    public bool CanBake(TransformTargetId target)
    {
        if (_pending != null)
            return false;
        if (target.Bone is not { } boneId)
            return false;
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return false;
        if (_posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return false;
        return AffectedBones(endpoint, config).Count > 1 &&
               HasSolveInput(endpoint, config);
    }

    /// <summary>The bones the solver moves for this target — the limb a bake
    /// is about. The bake itself writes the whole skeleton; this is what the
    /// UI and the live scenario name. Empty when the target is not an armed,
    /// resolvable chain.</summary>
    public IReadOnlyList<IBone> AffectedChain(TransformTargetId target)
    {
        if (target.Bone is not { } boneId ||
            _bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint } ||
            _posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return Array.Empty<IBone>();
        return AffectedBones(endpoint, config);
    }

    /// <summary>
    /// Snapshots the whole skeleton while the solve is visible, disarms the
    /// chain, and schedules the snapshot's replay through the import path two
    /// ticks later. Ok means the bake is armed, not that it has landed —
    /// <see cref="Note"/> carries the outcome.
    /// </summary>
    public GestureResult Begin(TransformTargetId target)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("IK bake must run on the framework thread.");
        if (_pending != null)
            return GestureResult.Fail("A bake is already running.");
        // A live transform gesture owns these bones right now; baking under
        // it would interleave two writers on the same chain.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (target.Bone is not { } boneId)
            return GestureResult.Fail("IK bake requires a bone target.");
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return GestureResult.Fail(
                $"Bone {boneId.CanonicalName} did not resolve.");
        var actor = endpoint.Skeleton.Actor;
        if (_bindings.GetActorId(actor) is null)
            return GestureResult.Fail("This bone's actor has no stable binding.");

        var config = _posing.GetIkConfiguration(endpoint);
        if (config is not { Enabled: true })
            return GestureResult.Fail("This chain is not armed.");

        var affected = AffectedBones(endpoint, config);
        if (affected.Count <= 1)
            return GestureResult.Fail("This chain does not resolve on this skeleton.");
        if (!HasSolveInput(endpoint, config))
            return GestureResult.Fail("This chain has not solved anything yet.");

        var slots = _skeletons.GetSkeletons(actor);
        if (slots.Count == 0)
            return GestureResult.Fail("This actor has no skeleton to bake.");

        // The snapshot is taken HERE, while the chain is still solving: these
        // absolutes are the solved pose. It is never refreshed — the apply
        // tick only needs the BASIS to have caught up, not the target.
        var snapshot = _poseFiles.CreatePoseFile(slots);

        if (_posing.SetIkConfiguration(endpoint, config with { Enabled = false })
            is { } error)
            return GestureResult.Fail(error);

        // Disarming can leave a Fixed chain's skeleton with nothing at all to
        // update, and the pass that has to run before the apply is the only
        // thing that refreshes the basis it writes against.
        foreach (var slot in slots)
            _posing.HoldSkeletonUpdates(slot, ApplyDelayTicks);

        _pending = new Pending(target, actor, endpoint, config, snapshot);
        Note = (target, "Baking the solved limb into the pose…");
        _framework.RunOnTick(Apply, delayTicks: ApplyDelayTicks);
        return GestureResult.Ok();
    }

    /// <summary>
    /// The deferred half. By now one apply pass has run with the chain
    /// disarmed, so every bone's <c>LastRawTransform</c> is once again
    /// animated baseline plus its own stacks — the basis an import write
    /// assumes.
    /// </summary>
    private void Apply()
    {
        if (_pending is not { } pending)
            return;
        _pending = null;

        var result = ApplySnapshot(pending);
        if (result.Success)
        {
            Note = null;
            return;
        }

        var detail = result.Detail ?? "The IK bake failed.";
        _log.Warning($"IK bake failed: {detail}");
        Note = (pending.Target, $"Bake: {detail}");

        // Nothing was written. Put the chain back the way it was so the limb
        // does not sit in the unsolved pose. A Fixed chain recaptures its
        // target at the position it is in now, which is where the solve left
        // it a couple of frames ago.
        if (_bindings.GetBoneId(pending.Endpoint) is not null &&
            _posing.SetIkConfiguration(pending.Endpoint, pending.Config) is { } rearm)
            _log.Warning($"IK bake could not re-arm the chain: {rearm}");
    }

    private GestureResult ApplySnapshot(Pending pending)
    {
        try
        {
            if (_bindings.GetActorId(pending.Actor) is null)
                return GestureResult.Fail("The actor went away before the bake landed.");

            // PoseImportOptions.Default is Brio's interactive import shape and
            // the one the .pose loader uses: every component, every present
            // slot, NO reset-before-import (the file's absolutes are applied
            // over the live stacks, which is what makes the delta cancel), and
            // no model transform — a bake must not move the actor.
            var applied = _poses.ImportPose(
                pending.Actor,
                pending.Snapshot,
                PoseImportOptions.Default,
                "Bake IK");
            return applied.Success
                ? GestureResult.Ok()
                : GestureResult.Fail(applied.Detail ?? "The pose import failed.");
        }
        catch (Exception ex)
        {
            return GestureResult.Fail($"The IK bake threw: {ex.Message}");
        }
    }

    /// <summary>
    /// The bones the solver actually moves. Two Joint moves the resolved
    /// definition (joints, optional twists, endpoint); CCD moves the endpoint
    /// and its parents to the configured depth, so its set is the walked
    /// hierarchy — mirroring <c>IKService.GetBonesToDepth</c> — not the
    /// definition. Ordered root-first, deduplicated.
    /// </summary>
    private static List<IBone> AffectedBones(IBone endpoint, IkChainConfig config)
    {
        var result = new List<IBone>();
        void Add(IBone? bone)
        {
            if (bone == null || result.Contains(bone))
                return;
            result.Add(bone);
        }

        if (config.Solver == IkSolver.Ccd)
        {
            var walked = new List<IBone> { endpoint };
            var current = endpoint.ParentBone;
            while (current != null && walked.Count < config.CcdDepth + 1)
            {
                walked.Add(current);
                current = current.ParentBone;
            }
            for (var i = walked.Count - 1; i >= 0; i--)
                Add(walked[i]);
            return result;
        }

        if (IkChains.ForEndpoint(endpoint.BoneName) is not { } definition)
            return result;
        // Same skeleton, same partial as the endpoint — never another slot.
        var skeleton = endpoint.Skeleton as Skeleton;
        IBone? ByName(string? name) => name == null
            ? null
            : skeleton?.GetBoneByName(name, endpoint.PartialId);
        Add(ByName(definition.FirstJoint));
        Add(ByName(definition.FirstTwist));
        Add(ByName(definition.SecondJoint));
        Add(ByName(definition.SecondTwist));
        Add(endpoint);
        return result;
    }

    /// <summary>The runtime's own per-frame solve condition: a Fixed chain
    /// holds its captured target with or without an authored delta, a
    /// Relative one solves only for a translation.</summary>
    private bool HasSolveInput(IBone endpoint, IkChainConfig config) =>
        config.TargetMode == IkTargetMode.Fixed ||
        _posing.GetModification(endpoint) is { } modification &&
        modification.Position != System.Numerics.Vector3.Zero;

    /// <summary>A pending bake never outlives the session: the scheduled
    /// apply checks <c>_pending</c> and finds nothing.</summary>
    public void Dispose()
    {
        _pending = null;
        Note = null;
    }
}
