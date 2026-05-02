using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to change gaze state with undo/redo support.
/// </summary>
public class GazeHistoryAction : IHistoryAction
{
    private readonly IGazeService _gazeService;
    private readonly IActor _actor;
    private readonly GazeState _oldState;
    private readonly GazeState _newState;

    public string Description => "Change Gaze";

    public GazeHistoryAction(
        IGazeService gazeService,
        IActor actor,
        GazeState oldState,
        GazeState newState)
    {
        _gazeService = gazeService;
        _actor = actor;
        _oldState = oldState.Clone();
        _newState = newState.Clone();
    }

    public void Execute() => _gazeService.SetGazeState(_actor, _newState);
    public void Undo() => _gazeService.SetGazeState(_actor, _oldState);
}
