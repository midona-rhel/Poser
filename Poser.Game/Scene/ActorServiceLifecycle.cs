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
    private static PoseImportOptions RestoreOptions => new()
    {
        ApplyRotation = true,
        ApplyPosition = true,
        ApplyScale = true,
        ApplyModelTransform = false,
        SuppressHistory = true,
    };

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
        Bindings.StableBindingRegistry bindings)
    {
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
        return new ActorState(
            _posing.GetEffectiveTransform(target),
            _spawns.IsVisible(target),
            CapturePose(target));
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
            return slots.Count == 0 ? null : _poseFiles.CreatePoseFile(slots);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' pose could not be captured before the despawn: {ex.Message}");
            return null;
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
            _framework.RunOnTick(() => Attempt(actor, state, attempts), delayTicks: 1);
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
        var restored = _poses.ImportPose(
            actor, pose, RestoreOptions, $"Restore pose for {actor.Name}");
        if (!restored.Success)
            _log.Warning(
                $"SceneLifecycleHistory: '{actor.Name}' came back but its pose was refused: {restored.Detail}");
    }
}
