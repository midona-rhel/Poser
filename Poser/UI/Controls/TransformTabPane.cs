using Dalamud.Interface;
using Poser.Core;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.History;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for transform editing in the properties panel.
/// </summary>
public class TransformTabPane : ITabPane
{
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly TransformWidget _transformWidget;

    // Track bone transform frame-by-frame for incremental deltas
    private IBone? _trackingBone;
    private Transform? _lastFrameTransform;

    // Current entity context (set before Draw)
    private IEntity? _entity;

    public string Name => "Transform";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.ArrowsAlt;

    public TransformTabPane(
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IHistoryService historyService)
    {
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _historyService = historyService;
        _transformWidget = new TransformWidget();
        _transformWidget.OnTransformCommit += OnTransformCommit;
    }

    /// <summary>
    /// Sets the entity to display/edit. Call before Draw().
    /// </summary>
    public void SetEntity(IEntity? entity)
    {
        _entity = entity;
    }

    /// <summary>
    /// Whether this tab is enabled for the current entity.
    /// </summary>
    public bool IsEnabled => _entity is ITransformable;

    public void Draw()
    {
        Transform transform;
        bool canEdit;

        if (_entity is IActor actor)
        {
            transform = _posingService.GetEffectiveTransform(actor);
            canEdit = _animationService.IsFrozen(actor);
        }
        else if (_entity is IBone bone)
        {
            transform = (_trackingBone == bone && _lastFrameTransform.HasValue)
                ? _lastFrameTransform.Value
                : bone.Transform;
            canEdit = true;
        }
        else if (_entity is ITransformable)
        {
            transform = _entity.Transform;
            canEdit = false;
        }
        else
        {
            // No entity or non-transformable - show disabled dummy UI
            transform = Transform.Identity;
            canEdit = false;
        }

        // Draw widget - when _entity is null, this renders disabled state
        bool isDisabled = _entity == null;
        if (_transformWidget.Draw("transform", ref transform, !canEdit || isDisabled))
        {
            if (_entity is ITransformable)
            {
                ApplyTransform(_entity, transform);
            }
        }
        else
        {
            if (_trackingBone != null)
            {
                _trackingBone = null;
                _lastFrameTransform = null;
            }
        }
    }

    private void ApplyTransform(IEntity entity, Transform transform)
    {
        if (entity is IActor actor)
        {
            _posingService.SetTransformOverride(actor, transform);
        }
        else if (entity is IBone bone)
        {
            if (_trackingBone != bone)
            {
                _trackingBone = bone;
                _lastFrameTransform = bone.Transform;
            }

            var lastObserved = _lastFrameTransform ?? bone.Transform;
            _bonePosingService.ApplyTransform(bone, transform, lastObserved);
            _lastFrameTransform = transform;
        }
    }

    private void OnTransformCommit(Transform oldTransform, Transform newTransform)
    {
        if (_entity is IActor actor)
        {
            var action = new TransformActorAction(_posingService, actor, oldTransform, newTransform);
            _historyService.Push(action);
        }
        else if (_entity is IBone bone)
        {
            var action = new TransformBoneAction(_bonePosingService, bone, oldTransform, newTransform);
            _historyService.Record(action);
        }
    }
}
