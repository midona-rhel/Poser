using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using LegacyTransform = Poser.Transform;

namespace Poser.Game.Posing;

/// <summary>
/// Turns a live-solved IK chain into ordinary pose edits (Brio's "Set IK
/// Changes").
///
/// Poser's IK is live: the chain is re-solved every frame from the endpoint's
/// translation delta and the result exists only in the native pose. Disarming
/// therefore abandons the solved placement — the endpoint's delta degrades to
/// a raw model-space add and the limb snaps back.
///
/// The bake is two phases, for the same reason FacialPoseCapture is:
///   1. capture every affected chain bone's LastRawTransform while the solve
///      is still visible — the same basis PoseFileService exports — then
///      disarm the chain;
///   2. let the pose settle for two framework ticks, then write each captured
///      value against the bone's now-unsolved LastRawTransform, exactly as
///      loading a pose file does.
/// Reading and writing on the same tick would produce identity deltas and
/// change nothing.
///
/// The written stacks are plain deltas against the animated baseline, so
/// export, undo/redo and history behave as they do for a hand-authored pose;
/// the chain's tuning survives, only Enabled is turned off.
/// </summary>
public sealed class IkBakeCapture : IDisposable
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly TransformCommandService _transforms;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    /// <summary>Ticks to let the disarmed chain settle back to its unsolved
    /// shape before the captured absolutes are applied against it.</summary>
    private const int SettleTicks = 2;

    private sealed class PendingBake
    {
        public required ActorId Actor;
        public required BoneId Endpoint;
        public required List<(BoneId Bone, LegacyTransform Captured)> Captures;
        public int TicksRemaining = SettleTicks;
    }

    private PendingBake? _pending;

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
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>True between the two phases; the surface disables the control
    /// so nothing can re-arm or re-target the chain under the capture.</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Whether this endpoint has a solve worth baking: armed, its chain
    /// resolves, and the runtime's own solve condition holds (a Fixed chain
    /// always holds a captured target; a Relative one needs an authored
    /// translation).
    /// </summary>
    public bool CanBake(TransformTargetId target)
    {
        if (_pending != null || target.Bone is not { } boneId)
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
    /// Phase one: capture the solved chain and disarm it.
    /// </summary>
    public GestureResult Begin(TransformTargetId target)
    {
        if (_pending != null)
            return GestureResult.Fail("An IK bake is already in progress.");
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("IK bake must start on the framework thread.");
        // A live transform gesture owns these bones right now; baking under it
        // would interleave two writers on the same chain.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (target.Bone is not { } boneId)
            return GestureResult.Fail("IK bake requires a bone target.");
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return GestureResult.Fail(
                $"Bone {boneId.CanonicalName} did not resolve.");
        if (_bindings.GetActorId(endpoint.Skeleton.Actor) is not { } actorId)
            return GestureResult.Fail("This bone's actor has no stable binding.");

        var config = _posing.GetIkConfiguration(endpoint);
        if (config is not { Enabled: true })
            return GestureResult.Fail("This chain is not armed.");

        var affected = AffectedBones(endpoint, config);
        if (affected.Count <= 1)
            return GestureResult.Fail("This chain does not resolve on this skeleton.");
        if (!HasSolveInput(endpoint, config))
            return GestureResult.Fail("This chain has not solved anything yet.");

        var captures = new List<(BoneId, LegacyTransform)>(affected.Count);
        foreach (var bone in affected)
        {
            if (_bindings.GetBoneId(bone) is not { } id)
                return GestureResult.Fail(
                    $"Chain bone {bone.BoneName} has no stable binding.");
            // LastRawTransform is the pre-reparent absolute the exporter
            // stores, and the solver's output for every chain bone.
            captures.Add((id, bone.LastRawTransform));
        }

        if (_posing.SetIkConfiguration(endpoint, config with { Enabled = false })
            is { } error)
            return GestureResult.Fail(error);

        _pending = new PendingBake
        {
            Actor = actorId,
            Endpoint = boneId,
            Captures = captures,
        };
        return GestureResult.Ok();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_pending is not { } pending)
            return;
        if (--pending.TicksRemaining > 0)
            return;
        _pending = null;
        Complete(pending);
    }

    /// <summary>
    /// Phase two: re-validate, then apply through the ONE atomic transform
    /// authority. SetAbsoluteMany captures every target before writing, rolls
    /// the whole chain back on any failure, refuses to run under a live
    /// gesture, and records the single undoable history patch.
    /// </summary>
    private void Complete(PendingBake pending)
    {
        if (Revalidate(pending) is { } problem)
        {
            _log.Warning($"IK bake abandoned: {problem}");
            return;
        }

        var writes = new List<(TransformTargetId, Poser.Domain.Transforms.PoseTransform)>(
            pending.Captures.Count);
        foreach (var (boneId, captured) in pending.Captures)
            writes.Add((
                TransformTargetId.ForBone(boneId),
                new Poser.Domain.Transforms.PoseTransform(
                    captured.Position, captured.Rotation, captured.Scale)));

        // rawBaseline: the application basis is each bone's CURRENT
        // LastRawTransform — the settled, unsolved chain — exactly as a pose
        // file loads. The captured baseline (LastTransform) diverges on
        // reparented partials.
        var applied = _transforms.SetAbsoluteMany(writes, "Bake IK", rawBaseline: true);
        if (!applied.Success)
            _log.Warning($"IK bake abandoned: {applied.Detail}");
    }

    /// <summary>
    /// Anything that could make the captured values belong to a different
    /// body or a different solve: the actor generation, any bone binding, or
    /// the chain being re-armed under the capture. Returns a reason, or null
    /// when the capture is still valid.
    /// </summary>
    private string? Revalidate(PendingBake pending)
    {
        if (_bindings.Resolve(pending.Actor) is not { Success: true })
            return "the actor is no longer available";
        if (_bindings.Resolve(pending.Endpoint) is not { Success: true, Value: { } endpoint })
            return $"bone {pending.Endpoint.CanonicalName} was rebound";
        if (_posing.GetIkConfiguration(endpoint) is { Enabled: true })
            return "the chain was armed again";
        foreach (var (boneId, _) in pending.Captures)
            if (_bindings.Resolve(boneId) is not { Success: true })
                return $"bone {boneId.CanonicalName} was rebound";
        return null;
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

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
