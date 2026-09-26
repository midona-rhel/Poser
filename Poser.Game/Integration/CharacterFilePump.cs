using Dalamud.Plugin.Services;
using Poser.Application.Integration;

namespace Poser.Game.Integration;

/// <summary>Host lifetime for framework-driven character-file readiness.</summary>
public sealed class CharacterFilePump : IDisposable
{
    private readonly IFramework _framework;
    private readonly CharacterFileSession _files;
    private readonly Action<string> _failed;

    public CharacterFilePump(IFramework framework, CharacterFileSession files, Action<string> failed)
    {
        _framework = framework;
        _files = files;
        _failed = failed;
        _framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (_files.Advance() is { Success: false } result)
            _failed(result.Detail ?? "The character file could not be applied.");
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _files.CancelPendingSpawn();
    }
}
