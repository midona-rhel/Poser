using System;
using System.Linq;
using Dalamud.Plugin;
using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>The shared call guard of the three IPC clients: an unavailable
/// provider refuses without calling, and a provider exception becomes a
/// failed result instead of escaping into the caller.</summary>
internal static class IntegrationIpc
{
    internal static IntegrationResult Guarded(
        IntegrationAvailability availability, string what, Func<IntegrationResult> call)
    {
        if (!availability.Available)
            return IntegrationResult.Fail(availability.Detail);
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return IntegrationResult.Fail($"{what}: {ex.Message}");
        }
    }

    internal static IntegrationValue<T> Guarded<T>(
        IntegrationAvailability availability, string what, Func<IntegrationValue<T>> call)
    {
        if (!availability.Available)
            return IntegrationValue<T>.Fail(availability.Detail);
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return IntegrationValue<T>.Fail($"{what}: {ex.Message}");
        }
    }
}

/// <summary>
/// One provider's cached availability: installed and loaded, and answering a
/// supported API version. Rechecked at most every ten seconds;
/// <see cref="Expire"/> forces a fresh check at a click boundary.
/// </summary>
internal sealed class IpcAvailability
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly string _internalName;
    private readonly string _name;
    private readonly Func<string?> _versionGate;
    private DateTime _nextCheck = DateTime.MinValue;
    private IntegrationAvailability _current;

    public IpcAvailability(
        IDalamudPluginInterface pluginInterface, string internalName, string name, Func<string?> versionGate)
    {
        _pluginInterface = pluginInterface;
        _internalName = internalName;
        _name = name;
        _versionGate = versionGate;
        _current = new(false, $"{name} has not been checked yet.");
    }

    public IntegrationAvailability Current
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now >= _nextCheck)
            {
                _nextCheck = now + CheckInterval;
                _current = Check();
            }
            return _current;
        }
    }

    public void Expire() => _nextCheck = DateTime.MinValue;

    private IntegrationAvailability Check()
    {
        bool installed = _pluginInterface.InstalledPlugins.Any(
            plugin => plugin.InternalName == _internalName && plugin.IsLoaded);
        if (!installed)
            return new IntegrationAvailability(false, $"{_name} is not installed or not loaded.");
        try
        {
            return _versionGate() is { } mismatch
                ? new IntegrationAvailability(false, mismatch)
                : new IntegrationAvailability(true, $"{_name} is available.");
        }
        catch (Exception)
        {
            return new IntegrationAvailability(false, $"{_name} is not responding.");
        }
    }
}
