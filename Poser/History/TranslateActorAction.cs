using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to translate an actor's position with undo/redo support.
/// </summary>
public class TranslateActorAction : IHistoryAction
{
    private readonly IPosingService _posingService;
    private readonly ActorBase _actor;
    private readonly Transform _oldTransform;
    private readonly Transform _newTransform;

    public string Description => $"Move {_actor.Name}";

    public TranslateActorAction(
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

    public void Execute()
    {
        _posingService.SetTransformOverride(_actor, _newTransform);
    }

    public void Undo()
    {
        _posingService.SetTransformOverride(_actor, _oldTransform);
    }
}
