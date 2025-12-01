using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to freeze/unfreeze an actor's physics.
/// </summary>
public class FreezePhysicsAction : IHistoryAction
{
    private readonly IAnimationService _animationService;
    private readonly ActorBase _actor;
    private readonly bool _freeze;

    public string Description => _freeze
        ? $"Freeze Physics {_actor.Name}"
        : $"Unfreeze Physics {_actor.Name}";

    public FreezePhysicsAction(IAnimationService animationService, ActorBase actor, bool freeze)
    {
        _animationService = animationService;
        _actor = actor;
        _freeze = freeze;
    }

    public void Execute()
    {
        if (_freeze)
            _animationService.FreezePhysics(_actor);
        else
            _animationService.UnfreezePhysics(_actor);
    }

    public void Undo()
    {
        if (_freeze)
            _animationService.UnfreezePhysics(_actor);
        else
            _animationService.FreezePhysics(_actor);
    }
}
