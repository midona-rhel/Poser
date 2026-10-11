using Dalamud.Plugin.Services;
using Poser.Application.Integration;
using Poser.Application.Presentation;

namespace Poser.Game.Integration;

/// <summary>Host lifetime for framework-driven character-file readiness.</summary>
public sealed class CharacterFilePump : IDisposable
{
    private readonly IFramework _framework;
    private readonly CharacterFileSession _files;
    private readonly IUserNotices _notices;

    public CharacterFilePump(IFramework framework, CharacterFileSession files, IUserNotices notices)
    {
        _framework = framework;
        _files = files;
        _notices = notices;
        _framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (_files.Advance() is { Success: false } result)
            _notices.Failed("Import: " + (result.Detail ?? "The character file could not be applied."));
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _files.CancelPendingSpawn();
    }
}
