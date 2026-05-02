using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Poser.Entities;

namespace Poser.IPC;

/// <summary>
/// Penumbra IPC integration for mod collection management.
/// API Version: 5.10
/// </summary>
public class PenumbraService : IPCServiceBase, IPenumbraService
{
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    // IPC Subscribers
    private ICallGateSubscriber<(int, int)>? _apiVersion;
    private ICallGateSubscriber<Dictionary<Guid, string>>? _getCollections;
    private ICallGateSubscriber<int, (bool, bool, Guid)>? _getCollectionForObject;
    private ICallGateSubscriber<Guid, int, bool, bool, (int, string)>? _setCollectionForObject;
    private ICallGateSubscriber<int, int>? _redrawObject;

    protected override string PluginName => "Penumbra";
    protected override (int Major, int Minor) RequiredVersion => (5, 10);

    public PenumbraService(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objectTable,
        IPluginLog log) : base(pluginInterface)
    {
        _objectTable = objectTable;
        _log = log;

        try
        {
            // Subscribe to Penumbra IPC
            _apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion");
            _getCollections = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections");
            _getCollectionForObject = pluginInterface.GetIpcSubscriber<int, (bool, bool, Guid)>("Penumbra.GetCollectionForObject");
            _setCollectionForObject = pluginInterface.GetIpcSubscriber<Guid, int, bool, bool, (int, string)>("Penumbra.SetCollectionForObject");
            _redrawObject = pluginInterface.GetIpcSubscriber<int, int>("Penumbra.RedrawObject");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to subscribe to Penumbra IPC: {ex.Message}");
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

    public Dictionary<Guid, string> GetCollections()
    {
        if (!IsAvailable || _getCollections == null)
            return new Dictionary<Guid, string>();

        try
        {
            return _getCollections.InvokeFunc() ?? new Dictionary<Guid, string>();
        }
        catch (Exception ex)
        {
            _log.Warning($"Penumbra.GetCollections failed: {ex.Message}");
            return new Dictionary<Guid, string>();
        }
    }

    public Guid? GetCollectionForActor(IActor actor)
    {
        if (!IsAvailable || _getCollectionForObject == null)
            return null;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return null;

        try
        {
            var (objectValid, _, collectionId) = _getCollectionForObject.InvokeFunc(objectIndex);
            return objectValid ? collectionId : null;
        }
        catch (Exception ex)
        {
            _log.Warning($"Penumbra.GetCollectionForObject failed: {ex.Message}");
            return null;
        }
    }

    public void SetCollectionForActor(IActor actor, Guid collectionId)
    {
        if (!IsAvailable || _setCollectionForObject == null)
            return;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return;

        try
        {
            // Parameters: collectionId, objectIndex, allowInheritance, forceInheritance
            var (result, _) = _setCollectionForObject.InvokeFunc(collectionId, objectIndex, true, false);
            if (result != 0)
            {
                _log.Warning($"Penumbra.SetCollectionForObject returned error: {result}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Penumbra.SetCollectionForObject failed: {ex.Message}");
        }
    }

    public void RedrawActor(IActor actor)
    {
        if (!IsAvailable || _redrawObject == null)
            return;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return;

        try
        {
            _redrawObject.InvokeFunc(objectIndex);
        }
        catch (Exception ex)
        {
            _log.Warning($"Penumbra.RedrawObject failed: {ex.Message}");
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
        // IPC subscribers don't need explicit disposal
        base.Dispose();
    }
}
