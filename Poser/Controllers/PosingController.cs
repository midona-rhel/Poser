using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Entities;
using Poser.History;
using Poser.Services;

namespace Poser.Controllers;

/// <summary>
/// Controller for posing operations with automatic history tracking.
/// UI components should use this instead of calling services directly.
/// </summary>
public class PosingController : IPosingController
{
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IGazeService _gazeService;
    private readonly IActorSpawnService _spawnService;
    private readonly IHistoryService _historyService;

    // Speed change tracking for slider drags
    private readonly Dictionary<IActor, float> _speedChangeStarts = new();

    public PosingController(
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IGazeService gazeService,
        IActorSpawnService spawnService,
        IHistoryService historyService)
    {
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _gazeService = gazeService;
        _spawnService = spawnService;
        _historyService = historyService;
    }

    #region Actor Transforms

    public void SetActorTransform(IActor actor, Transform transform)
    {
        var oldTransform = _posingService.GetEffectiveTransform(actor);
        if (oldTransform == transform) return;

        var action = new TransformActorAction(_posingService, actor, oldTransform, transform);
        _historyService.Push(action);
    }

    public void SetActorPosition(IActor actor, Vector3 position)
    {
        var current = _posingService.GetEffectiveTransform(actor);
        if (current.Position == position) return;

        var newTransform = new Transform(position, current.Rotation, current.Scale);
        var action = new TransformActorAction(_posingService, actor, current, newTransform);
        _historyService.Push(action);
    }

    public void SetActorRotation(IActor actor, Quaternion rotation)
    {
        var current = _posingService.GetEffectiveTransform(actor);
        if (current.Rotation == rotation) return;

        var newTransform = new Transform(current.Position, rotation, current.Scale);
        var action = new TransformActorAction(_posingService, actor, current, newTransform);
        _historyService.Push(action);
    }

    public void ResetActorTransform(IActor actor)
    {
        if (!_posingService.HasTransformOverride(actor)) return;

        var oldTransform = _posingService.GetEffectiveTransform(actor);
        var originalTransform = _posingService.GetOriginalTransform(actor);

        _posingService.ClearTransformOverride(actor);

        // Record for undo
        var action = new TransformActorAction(_posingService, actor, oldTransform, originalTransform);
        _historyService.Record(action);
    }

    #endregion

    #region Bone Transforms

    public void ApplyBoneTransform(IBone bone, Transform delta, Transform? originalModification = null)
    {
        var oldMod = originalModification ?? _bonePosingService.GetModification(bone) ?? Transform.Identity;

        _bonePosingService.ApplyTransform(bone, delta, originalModification, TransformComponents.All);

        var newMod = _bonePosingService.GetModification(bone) ?? Transform.Identity;
        var action = new TransformBoneAction(_bonePosingService, bone, oldMod, newMod);
        _historyService.Record(action);
    }

    public void ResetBone(IBone bone)
    {
        if (!_bonePosingService.HasModifications(bone)) return;

        var action = new ResetBoneAction(_bonePosingService, bone);
        _historyService.Push(action);
    }

    public void ResetSkeleton(ISkeleton skeleton)
    {
        var actions = new List<IHistoryAction>();

        foreach (var bone in skeleton.Bones)
        {
            if (_bonePosingService.HasModifications(bone))
            {
                actions.Add(new ResetBoneAction(_bonePosingService, bone));
            }
        }

        if (actions.Count == 0) return;

        if (actions.Count == 1)
        {
            _historyService.Push(actions[0]);
        }
        else
        {
            var composite = new CompositeAction("Reset Skeleton", actions);
            _historyService.Push(composite);
        }
    }

    #endregion

    #region Animation Control

    public void ToggleFreeze(IActor actor)
    {
        var isFrozen = _animationService.IsFrozen(actor);
        SetFrozen(actor, !isFrozen);
    }

    public void SetFrozen(IActor actor, bool frozen)
    {
        if (_animationService.IsFrozen(actor) == frozen) return;

        var action = new FreezeAnimationAction(_animationService, _posingService, actor, frozen);
        _historyService.Push(action);
    }

    public void TogglePhysicsFreeze(IActor actor)
    {
        var isFrozen = _animationService.IsPhysicsFrozen(actor);
        SetPhysicsFrozen(actor, !isFrozen);
    }

    public void SetPhysicsFrozen(IActor actor, bool frozen)
    {
        if (_animationService.IsPhysicsFrozen(actor) == frozen) return;

        var action = new FreezePhysicsAction(_animationService, actor, frozen);
        _historyService.Push(action);
    }

    public void SetAnimationSpeed(IActor actor, float speed)
    {
        var oldSpeed = _animationService.GetSpeed(actor);
        if (oldSpeed == speed) return;

        var action = new SpeedChangeAction(_animationService, actor, oldSpeed, speed);
        _historyService.Push(action);
    }

    public void BeginSpeedChange(IActor actor)
    {
        _speedChangeStarts[actor] = _animationService.GetSpeed(actor);
    }

    public void EndSpeedChange(IActor actor, float finalSpeed)
    {
        if (!_speedChangeStarts.TryGetValue(actor, out var startSpeed))
        {
            startSpeed = 1.0f;
        }
        _speedChangeStarts.Remove(actor);

        if (startSpeed == finalSpeed) return;

        // Apply the speed (it's already applied from slider drag)
        // Just record for history
        var action = new SpeedChangeAction(_animationService, actor, startSpeed, finalSpeed);
        _historyService.Record(action);
    }

    public void SetAnimationTime(IActor actor, float time)
    {
        _animationService.SetAnimationTime(actor, time);
    }

    #endregion

    #region Gaze Control

    public void SetGazeMode(IActor actor, GazeTargetMode mode)
    {
        var oldState = _gazeService.GetGazeState(actor);
        if (oldState.Mode == mode) return;

        var newState = oldState.Clone();
        newState.Mode = mode;

        var action = new GazeHistoryAction(_gazeService, actor, oldState, newState);
        _historyService.Push(action);
    }

    public void SetGazeTargetType(IActor actor, GazeTargetType targetType)
    {
        var oldState = _gazeService.GetGazeState(actor);
        if (oldState.TargetType == targetType) return;

        var newState = oldState.Clone();
        newState.TargetType = targetType;

        var action = new GazeHistoryAction(_gazeService, actor, oldState, newState);
        _historyService.Push(action);
    }

    public void SetGazeTarget(IActor actor, IActor target)
    {
        var oldState = _gazeService.GetGazeState(actor);

        var newState = oldState.Clone();
        newState.Mode = GazeTargetMode.Entity;
        newState.TargetEntity = target;

        var action = new GazeHistoryAction(_gazeService, actor, oldState, newState);
        _historyService.Push(action);
    }

    public void ResetGaze(IActor actor)
    {
        var oldState = _gazeService.GetGazeState(actor);
        if (oldState.Mode == GazeTargetMode.None) return;

        var newState = new GazeState { Mode = GazeTargetMode.None };

        var action = new GazeHistoryAction(_gazeService, actor, oldState, newState);
        _historyService.Push(action);
    }

    #endregion

    #region Visibility

    public void ToggleActorVisibility(IActor actor)
    {
        var isVisible = _spawnService.IsVisible(actor);
        SetActorVisibility(actor, !isVisible);
    }

    public void SetActorVisibility(IActor actor, bool visible)
    {
        if (_spawnService.IsVisible(actor) == visible) return;

        var action = new VisibilityAction(_spawnService, actor, visible);
        _historyService.Push(action);
    }

    #endregion
}
