using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Poser.Entities;

namespace Poser.IPC;

/// <summary>
/// Glamourer IPC integration for appearance management.
/// API Version: 1.4
/// </summary>
public class GlamourerService : IPCServiceBase, IGlamourerService
{
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    // IPC Subscribers
    private ICallGateSubscriber<(int, int)>? _apiVersion;
    private ICallGateSubscriber<Dictionary<Guid, string>>? _getDesignList;
    private ICallGateSubscriber<Guid, int, uint, int>? _applyDesign;
    private ICallGateSubscriber<int, uint, int>? _revertState;

    // Glamourer lock code for our application
    private const uint LockCode = 0x706F7365; // "pose" in ASCII

    protected override string PluginName => "Glamourer";
    protected override (int Major, int Minor) RequiredVersion => (1, 4);

    public GlamourerService(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objectTable,
        IPluginLog log) : base(pluginInterface)
    {
        _objectTable = objectTable;
        _log = log;

        try
        {
            // Subscribe to Glamourer IPC
            _apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion");
            _getDesignList = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList");
            _applyDesign = pluginInterface.GetIpcSubscriber<Guid, int, uint, int>("Glamourer.ApplyDesign");
            _revertState = pluginInterface.GetIpcSubscriber<int, uint, int>("Glamourer.RevertState");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to subscribe to Glamourer IPC: {ex.Message}");
        }
    }

    protected override (int Major, int Minor)? GetAPIVersion()
    {
        try
        {
            return _apiVersion?.InvokeFunc();
        }
        catch
        {
            return null;
        }
    }

    public Dictionary<Guid, string> GetDesigns()
    {
        if (!IsAvailable || _getDesignList == null)
            return new Dictionary<Guid, string>();

        try
        {
            return _getDesignList.InvokeFunc() ?? new Dictionary<Guid, string>();
        }
        catch (Exception ex)
        {
            _log.Warning($"Glamourer.GetDesignList failed: {ex.Message}");
            return new Dictionary<Guid, string>();
        }
    }

    public void ApplyDesign(IActor actor, Guid designId)
    {
        if (!IsAvailable || _applyDesign == null)
            return;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return;

        try
        {
            // Parameters: designId, objectIndex, lockCode
            var result = _applyDesign.InvokeFunc(designId, objectIndex, LockCode);
            if (result != 0)
            {
                _log.Warning($"Glamourer.ApplyDesign returned error: {result}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Glamourer.ApplyDesign failed: {ex.Message}");
        }
    }

    public void RevertAppearance(IActor actor)
    {
        if (!IsAvailable || _revertState == null)
            return;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return;

        try
        {
            // Parameters: objectIndex, lockCode
            var result = _revertState.InvokeFunc(objectIndex, LockCode);
            if (result != 0)
            {
                _log.Warning($"Glamourer.RevertState returned error: {result}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Glamourer.RevertState failed: {ex.Message}");
        }
    }

    private int GetObjectIndex(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return -1;

        // Find the object in the table to get its index
        for (var i = 0; i < _objectTable.Length; i++)
        {
            var obj = _objectTable[i];
            if (obj != null && obj.Address == actor.Address)
            {
                return i;
            }
        }

        return -1;
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
