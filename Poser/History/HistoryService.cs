using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.History;

public class HistoryService : IHistoryService, IDisposable
{
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly IBonePosingService _bonePosingService;
    private readonly IPosingService _posingService;
    private readonly Stack<IHistoryAction> _undoStack = new();
    private readonly Stack<IHistoryAction> _redoStack = new();

    // Drag recording state
    private Dictionary<IEntity, Transform>? _dragStartTransforms;
    private IReadOnlyList<IEntity>? _dragEntities;

    public HistoryService(
        IGPoseService gPoseService,
        IEventBus eventBus,
        IBonePosingService bonePosingService,
        IPosingService posingService)
    {
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _bonePosingService = bonePosingService;
        _posingService = posingService;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<TransformDragStartedEvent>(OnTransformDragStarted);
        _eventBus.Subscribe<TransformDragEndedEvent>(OnTransformDragEnded);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            // Clear history when exiting GPose
            Clear();
            _dragStartTransforms = null;
            _dragEntities = null;
        }
    }

    private void OnTransformDragStarted(TransformDragStartedEvent e)
    {
        _dragEntities = e.Entities;
        _dragStartTransforms = CaptureTransforms(e.Entities);
    }

    private void OnTransformDragEnded(TransformDragEndedEvent e)
    {
        if (_dragStartTransforms == null || _dragEntities == null || _dragEntities.Count == 0)
        {
            _dragStartTransforms = null;
            _dragEntities = null;
            return;
        }

        var endTransforms = CaptureTransforms(_dragEntities);

        // Check if anything actually changed
        bool hasChanges = false;
        foreach (var entity in _dragEntities)
        {
            if (_dragStartTransforms.TryGetValue(entity, out var start) &&
                endTransforms.TryGetValue(entity, out var end))
            {
                if (!TransformsEqual(start, end))
                {
                    hasChanges = true;
                    break;
                }
            }
        }

        if (hasChanges)
        {
            // Create and record the action
            var action = new TransformHistoryAction(
                _dragEntities.ToList(),
                _dragStartTransforms,
                endTransforms,
                _bonePosingService,
                _posingService);

            Record(action);
        }

        _dragStartTransforms = null;
        _dragEntities = null;
    }

    private Dictionary<IEntity, Transform> CaptureTransforms(IReadOnlyList<IEntity> entities)
    {
        var transforms = new Dictionary<IEntity, Transform>();
        foreach (var entity in entities)
        {
            if (entity is IBone bone)
            {
                var mod = _bonePosingService.GetModification(bone);
                transforms[entity] = mod ?? Transform.Identity;
            }
            else if (entity is IActor actor)
            {
                transforms[entity] = _posingService.GetEffectiveTransform(actor);
            }
        }
        return transforms;
    }

    private static bool TransformsEqual(Transform a, Transform b)
    {
        const float epsilon = 0.0001f;
        return System.Numerics.Vector3.DistanceSquared(a.Position, b.Position) < epsilon &&
               System.Numerics.Quaternion.Dot(a.Rotation, b.Rotation) > 1f - epsilon &&
               System.Numerics.Vector3.DistanceSquared(a.Scale, b.Scale) < epsilon;
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;
    public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

    public event Action? OnHistoryChanged;

    public void Push(IHistoryAction action)
    {
        // Execute the action
        action.Execute();

        // Add to undo stack
        _undoStack.Push(action);

        // Clear redo stack (new action invalidates redo history)
        _redoStack.Clear();

        OnHistoryChanged?.Invoke();
    }

    public void Record(IHistoryAction action)
    {
        // Add to undo stack WITHOUT executing (action was already applied)
        _undoStack.Push(action);

        // Clear redo stack (new action invalidates redo history)
        _redoStack.Clear();

        OnHistoryChanged?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;

        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);

        OnHistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;

        var action = _redoStack.Pop();
        action.Execute();
        _undoStack.Push(action);

        OnHistoryChanged?.Invoke();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        OnHistoryChanged?.Invoke();
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<TransformDragStartedEvent>(OnTransformDragStarted);
        _eventBus.Unsubscribe<TransformDragEndedEvent>(OnTransformDragEnded);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// History action for transform changes (bones and actors).
/// </summary>
internal class TransformHistoryAction : IHistoryAction
{
    private readonly List<IEntity> _entities;
    private readonly Dictionary<IEntity, Transform> _oldTransforms;
    private readonly Dictionary<IEntity, Transform> _newTransforms;
    private readonly IBonePosingService _bonePosingService;
    private readonly IPosingService _posingService;

    public TransformHistoryAction(
        List<IEntity> entities,
        Dictionary<IEntity, Transform> oldTransforms,
        Dictionary<IEntity, Transform> newTransforms,
        IBonePosingService bonePosingService,
        IPosingService posingService)
    {
        _entities = entities;
        _oldTransforms = new Dictionary<IEntity, Transform>(oldTransforms);
        _newTransforms = new Dictionary<IEntity, Transform>(newTransforms);
        _bonePosingService = bonePosingService;
        _posingService = posingService;
    }

    public string Description
    {
        get
        {
            if (_entities.Count == 1)
            {
                var entity = _entities[0];
                if (entity is IBone bone)
                    return $"Transform {bone.Name}";
                if (entity is IActor actor)
                    return $"Transform {actor.Name}";
                return $"Transform {entity.Name}";
            }
            return $"Transform {_entities.Count} entities";
        }
    }

    public void Execute()
    {
        ApplyTransforms(_newTransforms);
    }

    public void Undo()
    {
        ApplyTransforms(_oldTransforms);
    }

    private void ApplyTransforms(Dictionary<IEntity, Transform> transforms)
    {
        foreach (var entity in _entities)
        {
            if (!transforms.TryGetValue(entity, out var transform))
                continue;

            if (entity is IBone bone)
            {
                // For bones, we need to set the modification directly
                // The current implementation applies deltas, so we reset and apply
                _bonePosingService.ResetBone(bone);
                if (transform != Transform.Identity)
                {
                    _bonePosingService.ApplyTransform(bone, transform, null, TransformComponents.All);
                }
            }
            else if (entity is IActor actor)
            {
                _posingService.SetTransformOverride(actor, transform);
            }
        }
    }
}
