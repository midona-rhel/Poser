using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to toggle actor visibility with undo/redo support.
/// </summary>
public class VisibilityAction : IHistoryAction
{
    private readonly IActorSpawnService _spawnService;
    private readonly IActor _actor;
    private readonly bool _visible;

    public string Description => _visible ? "Show Actor" : "Hide Actor";

    public VisibilityAction(
        IActorSpawnService spawnService,
        IActor actor,
        bool visible)
    {
        _spawnService = spawnService;
        _actor = actor;
        _visible = visible;
    }

    public void Execute() => _spawnService.SetVisibility(_actor, _visible);
    public void Undo() => _spawnService.SetVisibility(_actor, !_visible);
}
