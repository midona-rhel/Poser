using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Game.Bindings;
using Poser.Documents.Appearance;
using static Poser.Game.Integration.IntegrationIpc;

namespace Poser.Game.Integration;

/// <summary>
/// Calls Customize+ API 6.x. Each actor operation looks up the current game
/// object index from Poser's actor ID before calling the provider.
/// </summary>
public sealed class CustomizePlusIpc : ICustomizePlusPort
{
    private const int CustomizeEcSuccess = 0;
    private const int CustomizeEcInvalidCharacter = 1;
    private const int CustomizeEcProfileNotFound = 3;

    private readonly IntegrationActorResolution _actors;
    private readonly IpcAvailability _availability;

    private readonly ICallGateSubscriber<(int Breaking, int Feature)> _customizeVersion;
    private readonly ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>> _getProfileList;
    private readonly ICallGateSubscriber<Guid, (int, string?)> _getProfileByUniqueId;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> _getActiveProfileId;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> _setTemporaryProfile;
    private readonly ICallGateSubscriber<Guid, int> _deleteTemporaryProfileById;

    public CustomizePlusIpc(
        IDalamudPluginInterface pluginInterface,
        IntegrationActorResolution actors)
    {
        _actors = actors;

        _customizeVersion = pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        _getProfileList = pluginInterface.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>("CustomizePlus.Profile.GetList");
        _getProfileByUniqueId = pluginInterface.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        _getActiveProfileId = pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        _setTemporaryProfile = pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        _deleteTemporaryProfileById = pluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");

        _availability = new IpcAvailability(pluginInterface, "CustomizePlus", "Customize+", () =>
        {
            var (breaking, _) = _customizeVersion.InvokeFunc();
            return breaking == 6
                ? null
                : $"Customize+'s API v{breaking} is not supported (needs v6).";
        });
    }

    public IntegrationAvailability CustomizePlus => _availability.Current;

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetBodyProfiles() =>
        Guarded(CustomizePlus, "Profiles", () =>
        {
            var profiles = _getProfileList.InvokeFunc();
            IReadOnlyList<ExternalItem> items = profiles
                .Select(profile => new ExternalItem(profile.Item1, profile.Item2))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(items);
        });

    public IntegrationValue<Guid?> GetActiveSavedBodyProfile(ActorId actor) =>
        Guarded(CustomizePlus, "Read saved profile", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<Guid?>.Fail(detail!);
            var (ec, active) = _getActiveProfileId.InvokeFunc((ushort)index);
            return ec switch
            {
                CustomizeEcSuccess => IntegrationValue<Guid?>.Ok(active),
                CustomizeEcProfileNotFound => IntegrationValue<Guid?>.Ok(null),
                _ => IntegrationValue<Guid?>.Fail($"Customize+ failed reading the active profile (code {ec})."),
            };
        });

    public IntegrationValue<string> GetBodyProfileJson(Guid profile) =>
        Guarded(CustomizePlus, "Profile data", () =>
        {
            var (ec, json) = _getProfileByUniqueId.InvokeFunc(profile);
            return ec == CustomizeEcSuccess && json != null
                ? IntegrationValue<string>.Ok(json)
                : IntegrationValue<string>.Fail(
                    $"Customize+ failed reading the profile (code {ec}).");
        });

    public IntegrationValue<Guid> ApplyTemporaryBodyProfile(ActorId actor, string profileJson) =>
        Guarded(CustomizePlus, "Apply profile", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<Guid>.Fail(detail!);
            var (ec, created) = _setTemporaryProfile.InvokeFunc((ushort)index, profileJson);
            return ec == CustomizeEcSuccess && created is { } id
                ? IntegrationValue<Guid>.Ok(id)
                : IntegrationValue<Guid>.Fail(
                    $"Customize+ failed applying the temporary profile (code {ec}).");
        });

    public IntegrationResult DeleteTemporaryBodyProfileById(Guid profile) =>
        Guarded(CustomizePlus, "Delete profile", () =>
        {
            int ec = _deleteTemporaryProfileById.InvokeFunc(profile);
            // Customize+ may have already removed the profile or its actor.
            // In either case, there is nothing left for Poser to delete.
            return ec is CustomizeEcSuccess or CustomizeEcProfileNotFound
                    or CustomizeEcInvalidCharacter
                ? IntegrationResult.Ok()
                : IntegrationResult.Fail(
                    $"Customize+ failed deleting the temporary profile (code {ec}).");
        });
}
