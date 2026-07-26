using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Poser.Game.Presentation;

/// <summary>
/// Narrow OUTBOUND Glamourer navigation: one action that opens
/// Glamourer's main window on the selected actor, and nothing else — no
/// state queries, no design application, no caching or mirroring of
/// appearance. Glamourer stays authoritative for persistent appearance.
///
/// Verified against the installed Glamourer 1.6.1.7 API assembly
/// (API version 1.8): label <c>Glamourer.OpenActorIndex</c> is
/// <c>Action&lt;int objectIndex&gt;</c> — "Open Glamourer's main window
/// to the actors tab, selecting a specific actor"; a negative or unknown
/// index silently keeps the current selection, so actor resolution is
/// checked HERE at the click boundary rather than through a return code
/// the endpoint does not have. The gate is <c>Glamourer.ApiVersion.V2</c>
/// with major 1 and minor &gt;= 8, the floor verified to carry the
/// Open* endpoints. Consumed through Dalamud's raw call gates so no
/// package dependency (and no restore) is involved.
/// </summary>
public sealed unsafe class GlamourerBridge
{
    private const int RequiredMajor = 1;
    private const int RequiredMinor = 8;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly StableBindingRegistry _bindings;
    private readonly ICallGateSubscriber<(int Major, int Minor)> _apiVersion;
    private readonly ICallGateSubscriber<int, object?> _openActorIndex;

    private DateTime _nextCheckUtc = DateTime.MinValue;
    private bool _available;
    private string _reason = "Glamourer has not been checked yet.";

    public GlamourerBridge(
        IDalamudPluginInterface pluginInterface,
        StableBindingRegistry bindings)
    {
        _pluginInterface = pluginInterface;
        _bindings = bindings;
        _apiVersion = pluginInterface.GetIpcSubscriber<(int Major, int Minor)>("Glamourer.ApiVersion.V2");
        _openActorIndex = pluginInterface.GetIpcSubscriber<int, object?>("Glamourer.OpenActorIndex");
    }

    /// <summary>
    /// Truthful availability with the reason a disabled action shows.
    /// Cached briefly (the reference's cadence) so per-frame UI reads do
    /// not spam IPC.
    /// </summary>
    public bool IsAvailable(out string reason)
    {
        var now = DateTime.UtcNow;
        if (now >= _nextCheckUtc)
        {
            _nextCheckUtc = now + CheckInterval;
            (_available, _reason) = Check();
        }
        reason = _reason;
        return _available;
    }

    private (bool Available, string Reason) Check()
    {
        bool installed = _pluginInterface.InstalledPlugins.Any(
            plugin => plugin.InternalName == "Glamourer" && plugin.IsLoaded);
        if (!installed)
            return (false, "Glamourer is not installed or not loaded.");

        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();
            if (major != RequiredMajor || minor < RequiredMinor)
                return (false,
                    $"Glamourer's API {major}.{minor} does not support opening an actor (needs {RequiredMajor}.{RequiredMinor}).");
        }
        catch (Exception)
        {
            return (false, "Glamourer is not responding.");
        }
        return (true, "Open this actor in Glamourer.");
    }

    /// <summary>
    /// Opens Glamourer on the actor. The object index is resolved from
    /// the stable id ONLY here, at the click boundary — a stale
    /// selection fails visibly instead of silently keeping Glamourer's
    /// current selection.
    /// </summary>
    public (bool Success, string? Detail) OpenActor(ActorId actor)
    {
        // Force a fresh availability check at the click.
        _nextCheckUtc = DateTime.MinValue;
        if (!IsAvailable(out var reason))
            return (false, reason);

        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
            return (false, resolved.Detail ?? "The actor is no longer available.");

        int index = ((CSCharacter*)legacy.Address)->GameObject.ObjectIndex;
        try
        {
            _openActorIndex.InvokeAction(index);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Glamourer did not respond: {ex.Message}");
        }
    }
}
