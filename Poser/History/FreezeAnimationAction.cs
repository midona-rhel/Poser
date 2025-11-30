using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to freeze/unfreeze an actor's animation.
/// </summary>
public class FreezeAnimationAction : IHistoryAction
{
    private readonly IAnimationService _animationService;
    private readonly ActorBase _actor;
    private readonly bool _freeze;

    public string Description => _freeze
        ? $"Freeze {_actor.Name}"
        : $"Unfreeze {_actor.Name}";

    public FreezeAnimationAction(IAnimationService animationService, ActorBase actor, bool freeze)
    {
        _animationService = animationService;
        _actor = actor;
        _freeze = freeze;
    }

    public void Execute()
    {
        if (_freeze)
            _animationService.Freeze(_actor);
        else
            _animationService.Unfreeze(_actor);
    }

    public void Undo()
    {
        // Undo is the opposite action
        if (_freeze)
            _animationService.Unfreeze(_actor);
        else
            _animationService.Freeze(_actor);
    }
}
