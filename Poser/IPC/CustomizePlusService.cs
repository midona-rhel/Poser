using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Poser.Entities;

namespace Poser.IPC;

/// <summary>
/// CustomizePlus IPC integration for body scaling profiles.
/// API Version: 6.4
/// </summary>
public class CustomizePlusService : IPCServiceBase, ICustomizePlusService
{
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    // IPC Subscribers
    private ICallGateSubscriber<(int, int)>? _apiVersion;
    private ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>? _getProfileList;
    private ICallGateSubscriber<ushort, (int, Guid?)>? _getActiveProfile;
    private ICallGateSubscriber<ushort, string, (int, Guid?)>? _setProfile;
    private ICallGateSubscriber<ushort, int>? _deleteProfile;

    protected override string PluginName => "CustomizePlus";
    protected override (int Major, int Minor) RequiredVersion => (6, 4);

    public CustomizePlusService(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objectTable,
        IPluginLog log) : base(pluginInterface)
    {
        _objectTable = objectTable;
        _log = log;

        try
        {
            // Subscribe to CustomizePlus IPC (namespaced IPC)
            _apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
            _getProfileList = pluginInterface.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>("CustomizePlus.Profile.GetList");
            _getActiveProfile = pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
            _setProfile = pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
            _deleteProfile = pluginInterface.GetIpcSubscriber<ushort, int>("CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to subscribe to CustomizePlus IPC: {ex.Message}");
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

    public IReadOnlyList<(Guid Id, string Name)> GetProfiles()
    {
        if (!IsAvailable || _getProfileList == null)
            return Array.Empty<(Guid, string)>();

        try
        {
            var profiles = _getProfileList.InvokeFunc();
            if (profiles == null)
                return Array.Empty<(Guid, string)>();

            // Extract just ID and Name from the tuple
            return profiles
                .Select(p => (p.Item1, p.Item2))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Warning($"CustomizePlus.Profile.GetList failed: {ex.Message}");
            return Array.Empty<(Guid, string)>();
        }
    }

    public Guid? GetActiveProfile(IActor actor)
    {
        if (!IsAvailable || _getActiveProfile == null)
            return null;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return null;

        try
        {
            var (errorCode, profileId) = _getActiveProfile.InvokeFunc((ushort)objectIndex);
            if (errorCode != 0)
            {
                return null;
            }
            return profileId;
        }
        catch (Exception ex)
        {
            _log.Warning($"CustomizePlus.Profile.GetActiveProfileIdOnCharacter failed: {ex.Message}");
            return null;
        }
    }

    public void SetProfile(IActor actor, Guid profileId)
    {
        if (!IsAvailable || _setProfile == null)
            return;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return;

        try
        {
            // Get profile data by ID to get the base64 string
            var profiles = _getProfileList?.InvokeFunc();
            var profile = profiles?.FirstOrDefault(p => p.Item1 == profileId);
            if (profile == null)
            {
                _log.Warning($"CustomizePlus profile not found: {profileId}");
                return;
            }

            // Parameters: objectIndex, base64ProfileData
            var (errorCode, _) = _setProfile.InvokeFunc((ushort)objectIndex, profile.Value.Item3);
            if (errorCode != 0)
            {
                _log.Warning($"CustomizePlus.Profile.SetTemporaryProfileOnCharacter returned error: {errorCode}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"CustomizePlus.Profile.SetTemporaryProfileOnCharacter failed: {ex.Message}");
        }
    }

    public void ClearProfile(IActor actor)
    {
        if (!IsAvailable || _deleteProfile == null)
            return;

        var objectIndex = GetObjectIndex(actor);
        if (objectIndex < 0)
            return;

        try
        {
            var errorCode = _deleteProfile.InvokeFunc((ushort)objectIndex);
            if (errorCode != 0)
            {
                _log.Warning($"CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter returned error: {errorCode}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter failed: {ex.Message}");
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
