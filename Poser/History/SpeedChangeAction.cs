using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to change animation speed with undo/redo support.
/// </summary>
public class SpeedChangeAction : IHistoryAction
{
    private readonly IAnimationService _animationService;
    private readonly IActor _actor;
    private readonly float _oldSpeed;
    private readonly float _newSpeed;

    public string Description => "Change Speed";

    public SpeedChangeAction(
        IAnimationService animationService,
        IActor actor,
        float oldSpeed,
        float newSpeed)
    {
        _animationService = animationService;
        _actor = actor;
        _oldSpeed = oldSpeed;
        _newSpeed = newSpeed;
    }

    public void Execute() => _animationService.SetSpeed(_actor, _newSpeed);
    public void Undo() => _animationService.SetSpeed(_actor, _oldSpeed);
}
