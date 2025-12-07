using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to apply or stop a base animation override.
/// </summary>
public class BaseAnimationAction : IHistoryAction
{
    private readonly IAnimationService _animationService;
    private readonly IActor _actor;
    private readonly ushort? _oldTimelineId;
    private readonly ushort? _newTimelineId;

    public string Description => _newTimelineId.HasValue
        ? $"Play Animation {_newTimelineId}"
        : "Stop Animation";

    /// <summary>
    /// Create action to apply a new base animation.
    /// </summary>
    public BaseAnimationAction(
        IAnimationService animationService,
        IActor actor,
        ushort? oldTimelineId,
        ushort? newTimelineId)
    {
        _animationService = animationService;
        _actor = actor;
        _oldTimelineId = oldTimelineId;
        _newTimelineId = newTimelineId;
    }

    public void Execute()
    {
        if (_newTimelineId.HasValue)
        {
            _animationService.ApplyBaseAnimation(_actor, _newTimelineId.Value, true);
        }
        else
        {
            _animationService.StopBaseAnimation(_actor);
        }
    }

    public void Undo()
    {
        if (_oldTimelineId.HasValue)
        {
            _animationService.ApplyBaseAnimation(_actor, _oldTimelineId.Value, true);
        }
        else
        {
            _animationService.StopBaseAnimation(_actor);
        }
    }
}
