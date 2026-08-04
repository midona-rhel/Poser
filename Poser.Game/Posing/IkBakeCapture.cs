using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Turns a live-solved IK chain into ordinary pose edits (Brio's "Set IK
/// Changes").
///
/// Poser's IK is live: the chain is re-solved every frame inside the native
/// skeleton update and the result exists only in the native pose. Disarming
/// therefore abandons the solved placement — the endpoint's delta degrades to
/// a raw model-space add and the limb snaps back.
///
/// The bake is ONE tick, the same shape as Brio's <c>ResetIK</c>, which
/// exports the solved chain and re-imports it in the same frame with no
/// settle wait. What makes that correct is not the timing but the BASIS: a
/// baked value has to be expressed against the bone's animated baseline —
/// the model transform the apply pass read before any stack or the solver
/// touched the bone — because that is the value the pass will start from
/// again on every later frame. Brio reaches that basis by clearing the
/// stacks, resetting the pose and letting its importer run inside the pass;
/// Poser's apply pass records the same value per bone every frame
/// (<c>BoneEvaluationObservation.AnimatedBaseline</c>), so the bake can read
/// it directly and write on the spot.
///
/// Diffing against anything else is what makes a chain leave its solved
/// placement: the bone's CURRENT transform gives an identity delta, and a
/// transform cached on an earlier frame carries whatever happened in
/// between — a settle pass that never ran (the runtime only re-evaluates
/// skeletons that still hold stacks or an armed chain, and disarming can
/// remove both), or the animation advancing underneath.
///
/// The written stacks are plain deltas against the animated baseline, so
/// export, undo/redo and history behave as they do for a hand-authored pose;
/// the chain's tuning survives, only Enabled is turned off.
/// </summary>
public sealed class IkBakeCapture
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly TransformCommandService _transforms;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    public IkBakeCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        IBonePosingService posing,
        TransformCommandService transforms,
        TransformGestureService gestures,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _posing = posing;
        _transforms = transforms;
        _gestures = gestures;
        _log = log;
    }

    /// <summary>
    /// Whether this endpoint has a solve worth baking: armed, its chain
    /// resolves, and the runtime's own solve condition holds (a Fixed chain
    /// always holds a captured target; a Relative one needs an authored
    /// translation).
    /// </summary>
    public bool CanBake(TransformTargetId target)
    {
        if (target.Bone is not { } boneId)
            return false;
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return false;
        if (_posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return false;
        return AffectedBones(endpoint, config).Count > 1 &&
               HasSolveInput(endpoint, config);
    }

    /// <summary>The bones a bake of this target would write, in the order it
    /// writes them; empty when the target is not an armed, resolvable
    /// chain.</summary>
    public IReadOnlyList<IBone> AffectedChain(TransformTargetId target)
    {
        if (target.Bone is not { } boneId ||
            _bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint } ||
            _posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return Array.Empty<IBone>();
        return AffectedBones(endpoint, config);
    }

    /// <summary>
    /// Captures the solved chain, disarms it, and writes the solve into the
    /// pose stacks — all on this tick, so no frame can pass between the
    /// value that is read and the basis it is written against.
    /// </summary>
    public GestureResult Begin(TransformTargetId target)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("IK bake must run on the framework thread.");
        // A live transform gesture owns these bones right now; baking under
        // it would interleave two writers on the same chain.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (target.Bone is not { } boneId)
            return GestureResult.Fail("IK bake requires a bone target.");
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return GestureResult.Fail(
                $"Bone {boneId.CanonicalName} did not resolve.");
        if (_bindings.GetActorId(endpoint.Skeleton.Actor) is null)
            return GestureResult.Fail("This bone's actor has no stable binding.");

        var config = _posing.GetIkConfiguration(endpoint);
        if (config is not { Enabled: true })
            return GestureResult.Fail("This chain is not armed.");

        var affected = AffectedBones(endpoint, config);
        if (affected.Count <= 1)
            return GestureResult.Fail("This chain does not resolve on this skeleton.");
        if (!HasSolveInput(endpoint, config))
            return GestureResult.Fail("This chain has not solved anything yet.");

        var writes = new List<(TransformTargetId, Poser.Domain.Transforms.PoseTransform)>(
            affected.Count);
        foreach (var bone in affected)
        {
            if (_bindings.GetBoneId(bone) is not { } id)
                return GestureResult.Fail(
                    $"Chain bone {bone.BoneName} has no stable binding.");
            // LastRawTransform is the pre-reparent absolute the exporter
            // stores, refreshed by the pass AFTER the solve — the solver's
            // output for every chain bone.
            var solved = bone.LastRawTransform;
            writes.Add((
                TransformTargetId.ForBone(id),
                new Poser.Domain.Transforms.PoseTransform(
                    solved.Position, solved.Rotation, solved.Scale)));
        }

        // Disarm first: the write below replaces the endpoint's stack, and
        // an armed chain would read that replacement as its next target.
        // Nothing re-evaluates the skeleton between these two statements —
        // both run inside one framework tick — so the observations the bake
        // reads are still the ones the last solved pass produced.
        if (_posing.SetIkConfiguration(endpoint, config with { Enabled = false })
            is { } error)
            return GestureResult.Fail(error);

        var applied = _transforms.BakeAbsoluteMany(writes, "Bake IK");
        if (applied.Success)
            return GestureResult.Ok();

        // Nothing was written: put the chain back exactly as it was, including
        // its Fixed capture, which re-arming recaptures from the still-solved
        // transform this same tick.
        if (_posing.SetIkConfiguration(endpoint, config) is { } rearm)
            _log.Warning($"IK bake could not re-arm the chain: {rearm}");
        return GestureResult.Fail(applied.Detail ?? "The IK bake failed.");
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
}
