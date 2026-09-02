using System;
using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The production <see cref="IActorLifecycle"/>: the spawn service for the
/// lifetime verbs, and — because an actor's restore outlives the frame that
/// starts it — the framework for the wait.
///
/// <para>WHY A WAIT. A respawn hands back its <see cref="IActor"/> at once,
/// but the body behind it is drawn several ticks later (Brio's
/// DrawWhenReady, ported in <c>ActorSpawnService</c>) and the skeleton a pose
/// lands on is built with that draw object. So the placement and the pose are
/// re-applied on the first tick the actor is posable, bounded by
/// <see cref="ReadyAttempts"/>, exactly the shape of the scene loader's own
/// readiness barrier.</para>
///
/// <para>WHY THE POSE IMPORT FILES NOTHING. Every other pose import IS a
/// history entry. This one is running INSIDE an undo, so an append would
/// clear the redo stack that undo had just pushed onto — the despawn could
/// then never be redone, and the entry left on top would strip the pose again
/// if the user pressed undo twice. <see cref="PoseImportOptions.SuppressHistory"/>
/// is set for that one reason and no other.</para>
/// </summary>
internal sealed class ActorServiceLifecycle : IActorLifecycle
{
    /// <summary>Ticks the restored body is given to become posable before the
    /// pose is given up on. Generous on purpose: a spawn's deferred draw plus
    /// whatever Penumbra and Glamourer do to it is the slowest thing the seam
    /// waits for, and a pose abandoned early is the user's work lost.</summary>
    private const int ReadyAttempts = 240;

    /// <summary>
    /// Absolute, whole-skeleton, and the model transform deliberately OFF:
    /// the placement is restored separately and explicitly, exactly as the
    /// scene loader separates <c>PlaceActor</c> from its pose import.
    /// </summary>
    private PoseImportOptions RestoreOptions => new()
    {
        ApplyRotation = DebugRotation,
        ApplyPosition = DebugPosition,
        ApplyScale = DebugScale,
        ApplyModelTransform = false,
        SuppressHistory = true,
    };

    // Debug-bridge knobs for the restore experiments (2026-09-02): which
    // components the restore imports and which side passes run.
    internal bool DebugRotation = true, DebugPosition = true, DebugScale = true;
    internal bool DebugPhysicsDeltas = true, DebugRootScales = true;

    private readonly IActorSpawnService _spawns;
    private readonly IPosingService _posing;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly CleanPoseFacade _poses;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGazeService _gaze;
    private readonly Poser.Application.Integration.ActorIntegrationSession
        _integration;
    private readonly Bindings.StableBindingRegistry _bindings;
    private readonly IBonePosingService _bonePosing;

    // Physics-driven bones (hair, clothing, the body-mod physics bones):
    // the game simulates them, so a captured pose must leave them alone —
    // pinning the source's momentary sway on a copy left its hair 35
    // degrees off and never settling (2026-09-02). A bone the user HAS
    // transformed on the source carries that transform across.
    private static readonly string[] PhysicsPrefixes =
        { "j_ex_h", "j_kami_", "j_ex_met_va", "j_sk_", "j_ex_top_", "j_ex_met_a", "j_ex_met_b", "j_ex_met_c", "j_ex_met_d", "j_zacc", "n_hijisoubi_", "n_hizasoubi_", "n_kataarmor_" };

    public ActorServiceLifecycle(
        IActorSpawnService spawns,
        IPosingService posing,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        CleanPoseFacade poses,
        IFramework framework,
        IPluginLog log,
        IGazeService gaze,
        Poser.Application.Integration.ActorIntegrationSession integration,
        Bindings.StableBindingRegistry bindings,
        IBonePosingService bonePosing)
    {
        _bonePosing = bonePosing;
        _spawns = spawns;
        _posing = posing;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _poses = poses;
        _framework = framework;
        _log = log;
        _gaze = gaze;
        _integration = integration;
        _bindings = bindings;
    }

    public bool IsSpawned(object actor) => _spawns.IsSpawnedActor((IActor)actor);

    public bool Destroy(object actor)
    {
        var target = (IActor)actor;
        // One verb for both provenances: the service routes an owned actor
        // through its ownership ledger and an adopted one through the gated
        // scene-table delete. An adopted actor first gets the same pre-delete
        // cleanup a scene clear gives it — gaze released, appearance
        // reverted — because after the delete there is nothing left to name.
        if (!_spawns.IsSpawnedActor(target))
            PrepareAdoptedRemoval(target);
        return _spawns.RemoveActorFromScene(target);
    }

    private void PrepareAdoptedRemoval(IActor actor)
    {
        try
        {
            _gaze.ResetGaze(actor);
        }
        catch (Exception ex)
        {
            Note($"'{actor.Name}': the gaze could not be released before " +
                $"removal ({ex.Message}).");
        }

        if (_bindings.GetActorId(actor) is not { } id)
            return;
        try
        {
            var reverted = _integration.ResetActor(id);
            if (!reverted.Success)
                Note($"'{actor.Name}': the appearance could not be reverted " +
                    $"before removal ({reverted.Detail ?? "the revert was refused"}).");
        }
        catch (Exception ex)
        {
            Note($"'{actor.Name}': the appearance could not be reverted " +
                $"before removal ({ex.Message}).");
        }
    }

    public ActorState Read(object actor)
    {
        var target = (IActor)actor;
        var pose = CapturePose(target);
        var rootScales = CapturePartialRootScales(target, pose);
        return new ActorState(
            _posing.GetEffectiveTransform(target),
            _spawns.IsVisible(target),
            pose)
        {
            PartialRootScales = rootScales,
            PhysicsDeltas = CapturePhysicsDeltas(target),
        };
    }

    /// <summary>The partial roots' own scales, and the file's child bones
    /// divided by each root's factor over its parent: the import applies a
    /// bone as a delta against the RAW animation, which never carries an
    /// owned root scale, so a child captured at 1.077 under a 1.077 root
    /// came back at 1.164 (01:3x). Divided out, the child's delta is 1 and
    /// the owned root supplies the scale once.</summary>
    private IReadOnlyDictionary<string, System.Numerics.Vector3>? CapturePartialRootScales(
        IActor actor, PoseFile? pose)
    {
        try
        {
            var scales = new Dictionary<string, System.Numerics.Vector3>();
            foreach (var skeleton in _skeletons.GetSkeletons(actor))
            {
                var factors = new Dictionary<int, System.Numerics.Vector3>();
                foreach (var bone in skeleton.Bones)
                {
                    if (!bone.IsPartialRoot || bone.IsSkeletonRoot)
                        continue;
                    var own = bone.LastTransform.Scale;
                    scales[$"{bone.PartialId}:{bone.BoneName}"] = own;
                    var parent = bone.ParentBone?.LastTransform.Scale ?? System.Numerics.Vector3.One;
                    factors[bone.PartialId] = new System.Numerics.Vector3(
                        parent.X == 0 ? 1f : own.X / parent.X,
                        parent.Y == 0 ? 1f : own.Y / parent.Y,
                        parent.Z == 0 ? 1f : own.Z / parent.Z);
                }
                if (pose == null || skeleton.Slot != global::Poser.Domain.Identity.PoseSlot.Character)
                    continue;
                foreach (var bone in skeleton.Bones)
                {
                    if (bone.IsPartialRoot || !factors.TryGetValue(bone.PartialId, out var factor)
                        || !pose.Bones.TryGetValue(bone.BoneName, out var data))
                        continue;
                    data.Scale = new System.Numerics.Vector3(
                        data.Scale.X / factor.X, data.Scale.Y / factor.Y, data.Scale.Z / factor.Z);
                }
            }
            return scales.Count == 0 ? null : scales;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPartialRootScales(IActor actor, IReadOnlyDictionary<string, System.Numerics.Vector3> scales)
    {
        foreach (var skeleton in _skeletons.GetSkeletons(actor))
            foreach (var bone in skeleton.Bones)
                if (bone.IsPartialRoot && !bone.IsSkeletonRoot
                    && scales.TryGetValue($"{bone.PartialId}:{bone.BoneName}", out var scale))
                    bone.PartialRootScale = scale;
    }

    /// <summary>
    /// The same synchronous whole-skeleton capture the scene SAVE makes
    /// (<c>SceneCaptureService</c>): one document, read off the caches the
    /// last update pass refreshed. The armed refresh <c>PoseExportCapture</c>
    /// performs is not available here — the actor is about to stop existing,
    /// so there is no later tick to write on.
    /// </summary>
    private PoseFile? CapturePose(IActor actor)
    {
        try
        {
            var slots = _skeletons.GetSkeletons(actor);
            return slots.Count == 0 ? null : _poseFiles.CreatePoseFile(slots, PoseBoneWanted);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' pose could not be captured before the despawn: {ex.Message}");
            return null;
        }
    }

    private static bool PoseBoneWanted(IBone bone) => !IsPhysicsDriven(bone.BoneName);

    private static bool IsPhysicsDriven(string name)
    {
        if (name.Contains("_phys", StringComparison.Ordinal) || name.Contains("_phy_", StringComparison.Ordinal))
            return true;
        foreach (var prefix in PhysicsPrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>A physics bone's LOCAL scale and offset against its parent
    /// on the source — where Customize+ puts its edits, inheritance and all,
    /// since every bone is read after propagation. Raw equals final on a
    /// foreign actor (Customize+ runs before Poser reads), so a delta cannot
    /// be read; the local frame can. Rotation stays with the simulation.</summary>
    private IReadOnlyDictionary<string, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale)>?
        CapturePhysicsDeltas(IActor actor)
    {
        try
        {
            var locals = new Dictionary<string, (System.Numerics.Vector3, System.Numerics.Quaternion, System.Numerics.Vector3)>();
            foreach (var skeleton in _skeletons.GetSkeletons(actor))
            {
                if (skeleton.Slot != global::Poser.Domain.Identity.PoseSlot.Character)
                    continue;
                foreach (var bone in skeleton.Bones)
                {
                    if (!IsPhysicsDriven(bone.BoneName) || bone.IsPartialRoot)
                        continue;
                    var parent = bone.ParentBone;
                    if (parent == null)
                        continue;
                    var final = bone.LastTransform;
                    var parentFinal = parent.LastTransform;
                    var localScale = new System.Numerics.Vector3(
                        parentFinal.Scale.X == 0 ? 1f : final.Scale.X / parentFinal.Scale.X,
                        parentFinal.Scale.Y == 0 ? 1f : final.Scale.Y / parentFinal.Scale.Y,
                        parentFinal.Scale.Z == 0 ? 1f : final.Scale.Z / parentFinal.Scale.Z);
                    var offset = System.Numerics.Vector3.Transform(
                        final.Position - parentFinal.Position,
                        System.Numerics.Quaternion.Inverse(parentFinal.Rotation));
                    var localOffset = new System.Numerics.Vector3(
                        parentFinal.Scale.X == 0 ? offset.X : offset.X / parentFinal.Scale.X,
                        parentFinal.Scale.Y == 0 ? offset.Y : offset.Y / parentFinal.Scale.Y,
                        parentFinal.Scale.Z == 0 ? offset.Z : offset.Z / parentFinal.Scale.Z);
                    locals[$"{bone.PartialId}:{bone.BoneName}"] = (localOffset, System.Numerics.Quaternion.Identity, localScale);
                }
            }
            return locals.Count == 0 ? null : locals;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One pass per tick: a child's target is computed from its
    /// parent as the LAST pass left it, so a chain settles a link per pass.</summary>
    private void ApplyPhysicsDeltasOver(
        IActor actor,
        IReadOnlyDictionary<string, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale)> locals,
        int passes)
    {
        ApplyPhysicsDeltas(actor, locals);
        if (passes <= 1)
            return;
        try
        {
            _framework.RunOnTick(() =>
            {
                if (actor.Address != nint.Zero)
                    ApplyPhysicsDeltasOver(actor, locals, passes - 1);
            }, delayTicks: 1);
        }
        catch (Exception ex)
        {
            _log.Warning($"SceneLifecycleHistory: physics frames on '{actor.Name}' could not continue: {ex.Message}");
        }
    }

    /// <summary>Puts each physics bone where the source's local frame says,
    /// against the COPY's parent: scale and offset owned as a modification
    /// on the bone's own simulated raw, rotation left to the simulation.
    /// A bone whose local frame already matches gets no modification.</summary>
    private void ApplyPhysicsDeltas(
        IActor actor,
        IReadOnlyDictionary<string, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale)> locals)
    {
        foreach (var skeleton in _skeletons.GetSkeletons(actor))
        {
            if (skeleton.Slot != global::Poser.Domain.Identity.PoseSlot.Character)
                continue;
            // Targets resolved top-down within the pass: a chain's deeper
            // links build on the parent's TARGET, not on where the last
            // frame left it (one link per pass otherwise, and it stalled).
            var targets = new Dictionary<IBone, Transform>();
            foreach (var bone in skeleton.Bones)
            {
                var parent = bone.ParentBone;
                if (parent == null
                    || !locals.TryGetValue($"{bone.PartialId}:{bone.BoneName}", out var local))
                    continue;
                var raw = bone.LastRawTransform;
                var parentNow = targets.TryGetValue(parent, out var parentTarget) ? parentTarget : parent.LastTransform;
                var targetScale = parentNow.Scale * local.Scale;
                var targetPosition = parentNow.Position + System.Numerics.Vector3.Transform(
                    local.Position * parentNow.Scale, parentNow.Rotation);
                var target = new Transform(targetPosition, raw.Rotation, targetScale);
                targets[bone] = target;
                if ((targetScale - raw.Scale).Length() < 0.001f && (targetPosition - raw.Position).Length() < 0.0005f)
                    continue;
                _bonePosing.ApplyTransform(bone, target, raw);
            }
        }
    }

    public void Restore(object actor, ActorState state)
    {
        // Visibility is a plain field write and needs no body, so it lands
        // now: a restored-hidden actor must never flash into view first.
        var target = (IActor)actor;
        _spawns.SetVisibility(target, state.Visible);
        Schedule(target, state, ReadyAttempts);
    }

    public void Note(string detail) => _log.Warning(detail);

    public void WhenPosable(object actor, Action<object> act) =>
        ScheduleReady((IActor)actor, act, ReadyAttempts);

    private void ScheduleReady(IActor actor, Action<object> act, int attempts)
    {
        if (attempts <= 0)
        {
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' never became posable, so a follow-up on it did not run.");
            return;
        }
        try
        {
            _framework.RunOnTick(() =>
            {
                if (actor.Address == nint.Zero)
                    return;
                if (!_poses.HasPosableSkeleton(actor) || _poses.IsImportBusy)
                {
                    ScheduleReady(actor, act, attempts - 1);
                    return;
                }
                try
                {
                    act(actor);
                }
                catch (Exception ex)
                {
                    _log.Warning(
                        $"SceneLifecycleHistory: a follow-up on '{actor.Name}' failed: {ex.Message}");
                }
            }, delayTicks: 1);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' follow-up could not be scheduled: {ex.Message}");
        }
    }

    private void Schedule(IActor actor, ActorState state, int attempts)
    {
        if (attempts <= 0)
        {
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' came back but never became posable, so its pose and placement were not restored.");
            return;
        }
        try
        {
            _framework.RunOnTick(() =>
            {
                // The tick runs as a task: an escaped exception here is
                // an unobserved-task error a minute later, not a report.
                try
                {
                    Attempt(actor, state, attempts);
                }
                catch (Exception ex)
                {
                    _log.Warning(
                        $"SceneLifecycleHistory: '{actor.Name}' came back but its restore failed: {ex.Message}");
                }
            }, delayTicks: 1);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' restore could not be scheduled: {ex.Message}");
        }
    }

    private void Attempt(IActor actor, ActorState state, int attempts)
    {
        // Gone again — a second despawn, a scene load, GPose ending. There is
        // nothing to restore onto and nothing to report.
        if (actor.Address == nint.Zero)
            return;
        if (state.Pose is not null &&
            (!_poses.HasPosableSkeleton(actor) || _poses.IsImportBusy))
        {
            Schedule(actor, state, attempts - 1);
            return;
        }

        _posing.SetTransformOverride(actor, state.Placement);
        if (_posing.GetTransformOverride(actor) is null)
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' came back but its placement was refused by the transform owner.");

        if (state.Pose is not { } pose)
            return;
        // Root scales one tick BEFORE the import: the import measures each
        // child against its root as the last posing pass left it, and a
        // root owned in the same tick compounded the face bones (1.077
        // twice, 01:2x). Own the roots, let a pass run, then import.
        if (state.PartialRootScales is { } rootScales && DebugRootScales)
            ApplyPartialRootScales(actor, rootScales);
        var restored = _poses.ImportPose(
            actor, pose, RestoreOptions, $"Restore pose for {actor.Name}");
        if (!restored.Success)
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' came back but its pose was refused: {restored.Detail}");
        if (state.PhysicsDeltas is { } physicsDeltas && DebugPhysicsDeltas)
            ApplyPhysicsDeltasOver(actor, physicsDeltas, passes: 4);
    }
}
