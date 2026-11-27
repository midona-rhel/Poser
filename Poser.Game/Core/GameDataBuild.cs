using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Poser.Game.Core;

/// <summary>
/// Builds one list from the game's data sheets: the animation, minion and
/// mount, and model lists on a worker thread the first time a picker needs
/// them (<see cref="EnsureLoaded"/>), and short lists such as the dyes or the
/// status icons on the calling thread the first time they are read
/// (<see cref="BuildNow"/>). The game's data does not change while the game
/// runs, so a list that was built is kept until Poser is unloaded. A build can
/// fail, for example when a game update changed a sheet; the failure is kept,
/// so the picker can say why the list is empty or short, until the person
/// opens the picker again.
/// </summary>
/// <param name="listName">The list's name in the failure text, such as "animation".</param>
/// <param name="build">Reads the sheets and publishes the list.</param>
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

    /// <summary>Builds again after a failure, on a worker thread. The interface
    /// calls this when a person opens the picker.</summary>
    public void Retry()
    {
        if (_state == Failed)
            Start();
    }

    /// <summary>Builds the list on the calling thread if it has not been built
    /// yet. A second thread that asks while the list is being built waits for
    /// it: the wardrobe is warmed on a worker thread at startup while the
    /// interface may already read it.</summary>
    public void BuildNow()
    {
        if (_state is Built or Failed)
            return;
        lock (_sync)
        {
            if (_state != NotStarted)
                return;
            _state = Building;
            Run();
        }
    }

    /// <summary>For a list built with <see cref="BuildNow"/>: forgets a failed
    /// build, so the next read reads the sheets again. The interface calls
    /// this when a person opens the picker.</summary>
    public void ForgetFailure()
    {
        lock (_sync)
            if (_state == Failed)
                _state = NotStarted;
    }

    private readonly object _sync = new();

    private void Start()
    {
        _state = Building;
        Task.Run(Run);
    }

    private void Run()
    {
        try
        {
            _failure = null;
            build();
            _state = Built;
        }
        catch (Exception ex)
        {
            log.Error($"The {listName} list could not be built from the game data: {ex}");
            _failure = $"The {listName} list could not be read from the game data: {ex.Message}";
            _state = Failed;
        }
    }
}
