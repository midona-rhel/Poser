using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// History action for transform changes, enabling undo/redo.
/// </summary>
public class TransformHistoryAction : IHistoryAction
{
    private readonly IPosingService _posingService;
    private readonly ActorBase _actor;
    private readonly Transform _oldTransform;
    private readonly Transform _newTransform;

    public string Description => "Transform Change";

    public TransformHistoryAction(
        IPosingService posingService,
        ActorBase actor,
        Transform oldTransform,
        Transform newTransform)
    {
        _posingService = posingService;
        _actor = actor;
        _oldTransform = oldTransform;
        _newTransform = newTransform;
    }

    public void Execute() => _posingService.SetTransformOverride(_actor, _newTransform);
    public void Undo() => _posingService.SetTransformOverride(_actor, _oldTransform);
}
