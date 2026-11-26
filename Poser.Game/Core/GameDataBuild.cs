using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Poser.Game.Core;

/// <summary>
/// Builds one list from the game's data sheets on a worker thread, the first
/// time a picker needs it: the animation list, the minion and mount list, and
/// the model list. The game's data does not change while the game runs, so a
/// list that was built is kept until Poser is unloaded. A build can fail, for
/// example when a game update changed a sheet; the failure is kept, so the
/// picker can say why it is empty, until the person opens the picker again.
/// </summary>
/// <param name="listName">The list's name in the failure text, such as "animation".</param>
/// <param name="build">Reads the sheets and publishes the list. Runs on a worker thread.</param>
public sealed class GameDataBuild(string listName, Action build, IPluginLog log)
{
    private const int NotStarted = 0;
    private const int Building = 1;
    private const int Built = 2;
    private const int Failed = 3;

    // Written by the worker thread, read by the interface.
    private volatile int _state;
    private volatile string? _failure;

    public bool IsBuilding => _state == Building;

    /// <summary>Why the last build failed, or null.</summary>
    public string? Failure => _failure;

    /// <summary>Starts the build if it has not run yet. The interface calls
    /// this every frame a picker could open, so it never starts a second
    /// build.</summary>
    public void EnsureLoaded()
    {
        if (_state == NotStarted)
            Start();
    }

    /// <summary>Builds again after a failure. The interface calls this when
    /// a person opens the picker.</summary>
    public void Retry()
    {
        if (_state == Failed)
            Start();
    }

    private void Start()
    {
        _state = Building;
        Task.Run(() =>
        {
            try
            {
                build();
                _failure = null;
                _state = Built;
            }
            catch (Exception ex)
            {
                log.Error($"The {listName} list could not be built from the game data: {ex}");
                _failure = $"The {listName} list could not be read from the game data: {ex.Message}";
                _state = Failed;
            }
        });
    }
}
