using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Spawned furniture still streaming its model, each with the deadline after
/// which the piece is released rather than left half-loaded.
/// </summary>
internal sealed class FurnitureLoadTracker
{
    private readonly IBgObjectPort _port;
    private readonly IPluginLog _log;
    private readonly Func<AdoptedWorldObject, bool> _isAdopted;
    private readonly Func<AdoptedWorldObject, bool> _release;
    private readonly Dictionary<AdoptedWorldObject, DateTime> _loading = new();

    public FurnitureLoadTracker(
        IBgObjectPort port,
        IPluginLog log,
        Func<AdoptedWorldObject, bool> isAdopted,
        Func<AdoptedWorldObject, bool> release)
    {
        _port = port;
        _log = log;
        _isAdopted = isAdopted;
        _release = release;
    }

    public void Track(AdoptedWorldObject handle, DateTime deadline) =>
        _loading[handle] = deadline;

    /// <summary>Whether a spawned furniture piece is still streaming its
    /// model.</summary>
    public bool IsLoading(AdoptedWorldObject handle) =>
        _loading.ContainsKey(handle) && !_port.IsBgReady(handle.Address);

    /// <summary>Keeps a piece that has not loaded: a scene load waited for it
    /// and named it, so the timed release below must not take it away behind
    /// that outcome. The night dressing still lands once it streams in.</summary>
    public void Keep(AdoptedWorldObject handle) =>
        _loading.Remove(handle);

    public void Pump(DateTime now)
    {
        foreach (var (handle, deadline) in _loading.ToArray())
        {
            if (!_isAdopted(handle) || _port.IsBgReady(handle.Address))
            {
                _loading.Remove(handle);
                continue;
            }
            if (now < deadline) continue;
            if (_release(handle))
            {
                _loading.Remove(handle);
                _log.Warning($"Furniture '{handle.Name}' did not finish loading and was removed.");
            }
        }
    }
}
