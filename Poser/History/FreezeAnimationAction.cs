using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to freeze/unfreeze an actor's animation.
/// Preserves transform state so unfreezing doesn't lose position.
/// </summary>
public class FreezeAnimationAction : IHistoryAction
{
    private readonly IAnimationService _animationService;
    private readonly IPosingService _posingService;
    private readonly IActor _actor;
    private readonly bool _freeze;
    private readonly Transform? _savedTransform;

    public string Description => _freeze
        ? $"Freeze {_actor.Name}"
        : $"Unfreeze {_actor.Name}";

    public FreezeAnimationAction(
        IAnimationService animationService,
        IPosingService posingService,
        IActor actor,
        bool freeze)
    {
        _animationService = animationService;
        _posingService = posingService;
        _actor = actor;
        _freeze = freeze;

        // Capture current transform when creating the action
        // This allows us to restore it on undo
        _savedTransform = _posingService.GetEffectiveTransform(actor);
    }

    public void Execute()
    {
        if (_freeze)
        {
            _animationService.Freeze(_actor);
        }
        else
        {
            _animationService.Unfreeze(_actor);
        }
    }

    public void Undo()
    {
        if (_freeze)
        {
            // Undoing a freeze = unfreeze, but restore the transform we had
            _animationService.Unfreeze(_actor);
            if (_savedTransform.HasValue)
            {
                _posingService.SetTransformOverride(_actor, _savedTransform.Value);
            }
        }
        else
        {
            // Undoing an unfreeze = freeze, restore the transform
            _animationService.Freeze(_actor);
            if (_savedTransform.HasValue)
            {
                _posingService.SetTransformOverride(_actor, _savedTransform.Value);
            }
        }
    }
}
