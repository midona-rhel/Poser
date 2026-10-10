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
/// Glamourer's raw, version-gated call gates, pinned against Glamourer.Api
/// 51c15bb (ApiVersion.V2 1.8+, the verified floor carrying the Open*
/// endpoints). Glamourer flag words: Once 0x1, Equipment 0x2,
/// Customization 0x4, Lock 0x8. Every actor-targeted call resolves the exact
/// stable generation to an object index at the call boundary.
/// </summary>
public sealed class GlamourerIpc : IGlamourerPort, ISpawnAppearancePort
{
    /// <summary>Poser's MCDF recovery key ("POSR"). Ordinary editing uses
    /// zero, so it cannot bypass our own MCDF hold. A keyed read may
    /// distinguish our hold from a foreign one without unlocking either.</summary>
    private const uint LockKey = 0x504F5352;

    private const ulong ApplyOnce = 0x1;
    private const ulong ApplyEquipment = 0x2;
    private const ulong ApplyCustomization = 0x4;
    private const ulong ApplyLock = 0x8;

    private const int GlamourerEcSuccess = 0;
    private const int GlamourerEcNothingDone = 1;
    /// <summary>No actor of that name is present. For a by-name release
    /// that is a completed release, not a failure.</summary>
    private const int GlamourerEcActorNotFound = 2;
    private const int GlamourerEcInvalidKey = 6;

    private readonly IntegrationActorResolution _actors;
    private readonly IObjectTable _objects;
    private readonly IpcAvailability _availability;

    private readonly ICallGateSubscriber<(int Major, int Minor)> _glamourerVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getDesignList;
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> _applyDesign;
    private readonly ICallGateSubscriber<int, uint, (int, string?)> _getStateBase64;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _applyState;
    private readonly ICallGateSubscriber<int, uint, int> _unlockState;
    // By NAME, for the exit edge: the GPose clone is destroyed there, but
    // Glamourer's locked state belongs to the character's IDENTITY and
    // outlives the object index. The pair mirrors the by-index unlock and
    // restore exactly — and is deliberately NOT Glamourer's RevertState*,
    // which reverts to GAME state: the clone and the player share one
    // identity, so a revert would throw away the design the user actually
    // had on every post-import exit.
    private readonly ICallGateSubscriber<string, uint, int> _unlockStateName;
    private readonly ICallGateSubscriber<object, string, uint, ulong, int> _applyStateName;
    private readonly ICallGateSubscriber<int, object?> _openActorIndex;
    private readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> _setItem;
    private readonly ICallGateSubscriber<int, byte, ulong, uint, ulong, int> _setBonusItem;
    private readonly ICallGateSubscriber<int, ulong, bool, uint, ulong, int> _setMetaState;
    private readonly ICallGateSubscriber<int, uint, (int, Newtonsoft.Json.Linq.JObject?)> _getState;
    private readonly ICallGateSubscriber<int, uint, ulong, int> _revertState;
    private readonly ICallGateSubscriber<string, ushort, uint, int> _deletePlayerState;
    private readonly ICallGateSubscriber<string, string, (int, Guid)> _addDesign;

    public GlamourerIpc(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objects,
        IntegrationActorResolution actors)
    {
        _actors = actors;
        _objects = objects;

        _glamourerVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        _getDesignList = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2");
        _applyDesign = pluginInterface.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        _getStateBase64 = pluginInterface.GetIpcSubscriber<int, uint, (int, string?)>("Glamourer.GetStateBase64");
        _applyState = pluginInterface.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");
        _unlockState = pluginInterface.GetIpcSubscriber<int, uint, int>("Glamourer.UnlockState");
        _unlockStateName = pluginInterface.GetIpcSubscriber<string, uint, int>("Glamourer.UnlockStateName");
        _applyStateName = pluginInterface.GetIpcSubscriber<object, string, uint, ulong, int>("Glamourer.ApplyStateName");
        _openActorIndex = pluginInterface.GetIpcSubscriber<int, object?>("Glamourer.OpenActorIndex");
        _setItem = pluginInterface.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
        _setBonusItem = pluginInterface.GetIpcSubscriber<int, byte, ulong, uint, ulong, int>("Glamourer.SetBonusItem");
        _setMetaState = pluginInterface.GetIpcSubscriber<int, ulong, bool, uint, ulong, int>("Glamourer.SetMetaState");
        _getState = pluginInterface.GetIpcSubscriber<int, uint, (int, Newtonsoft.Json.Linq.JObject?)>("Glamourer.GetState");
        _revertState = pluginInterface.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
        _deletePlayerState = pluginInterface.GetIpcSubscriber<string, ushort, uint, int>("Glamourer.DeletePlayerState");
        _addDesign = pluginInterface.GetIpcSubscriber<string, string, (int, Guid)>("Glamourer.AddDesign");

        _availability = new IpcAvailability(pluginInterface, "Glamourer", "Glamourer", () =>
        {
            var (major, minor) = _glamourerVersion.InvokeFunc();
            return major == 1 && minor >= 8
                ? null
                : $"Glamourer's API {major}.{minor} is not supported (needs 1.8).";
        });
    }

    public IntegrationAvailability Glamourer => _availability.Current;

    // ── Glamourer ────────────────────────────────────────────────────────

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetDesigns() =>
        Guarded(Glamourer, "Designs", () =>
        {
            var designs = _getDesignList.InvokeFunc();
            IReadOnlyList<ExternalItem> items = designs
                .Select(pair => new ExternalItem(pair.Key, pair.Value))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(items);
        });

    public IntegrationValue<string> CaptureGlamourerState(ActorId actor) =>
        Guarded(Glamourer, "Capture state", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<string>.Fail(detail!);
            var (ec, state) = _getStateBase64.InvokeFunc(index, 0u);
            if (ec == GlamourerEcInvalidKey)
                return GlamourerReadFailure<string>(actor, ec);
            if (ec != GlamourerEcSuccess || state == null)
                return IntegrationValue<string>.Fail(
                    $"Glamourer failed reading the actor state (code {ec}).");
            return IntegrationValue<string>.Ok(state);
        });

    public IntegrationResult ApplyDesign(ActorId actor, Guid design) =>
        Guarded(Glamourer, "Apply design", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            // The API's documented design default: Once | Equipment |
            // Customization — applied once, no persistent lock.
            int ec = _applyDesign.InvokeFunc(
                design, index, 0u, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return GlamourerResult(ec, "applying the design", actor);
        });

    public IntegrationResult HoldGlamourerState(ActorId actor, string state) =>
        Guarded(Glamourer, "Hold state", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            // Fixed + locked: without Once the state maps to IpcFixed, and
            // the Lock flag with Poser's key keeps automation off the
            // imported look until UnlockGlamourerState releases it.
            int ec = _applyState.InvokeFunc(
                state, index, LockKey, ApplyEquipment | ApplyCustomization | ApplyLock);
            return GlamourerResult(ec, "holding the actor state");
        });

    public IntegrationResult RestoreGlamourerState(ActorId actor, string state) =>
        Guarded(Glamourer, "Restore state", () =>
        {
            var access = ProbeGlamourerAccess(actor);
            if (access.Kind is GlamourerAccessKind.ForeignHeld or GlamourerAccessKind.Unavailable)
                return IntegrationResult.Refused(access);
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            // One-shot manual restoration: Once maps to IpcManual, no Lock
            // flag — after a restore no Poser fixed state or lock remains.
            int ec = _applyState.InvokeFunc(
                state, index, LockKey, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return GlamourerResult(ec, "restoring the actor state");
        });

    public IntegrationResult UnlockGlamourerState(ActorId actor) =>
        Guarded(Glamourer, "Unlock", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            int ec = _unlockState.InvokeFunc(index, LockKey);
            return ec is GlamourerEcSuccess or GlamourerEcNothingDone
                ? IntegrationResult.Ok()
                : GlamourerResult(ec, "releasing Poser's lock");
        });

    public IntegrationResult UnlockGlamourerStateByName(string name) =>
        GuardedByName(name, "Unlock by name", () =>
        {
            // Poser's key is the only thing that may release this state; a
            // foreign lock refuses with InvalidKey rather than being stolen.
            // An absent character has no state to unlock, which is a
            // completed release rather than a retryable failure — but it is
            // NOT an early exit from the caller's restore step, which owns
            // its own absent-character answer.
            int ec = _unlockStateName.InvokeFunc(name, LockKey);
            return ec is GlamourerEcSuccess or GlamourerEcNothingDone
                or GlamourerEcActorNotFound
                ? IntegrationResult.Ok()
                : GlamourerResult(ec, "releasing Poser's lock by name");
        });

    public IntegrationResult RestoreGlamourerStateByName(string name, string state) =>
        GuardedByName(name, "Restore state by name", () =>
        {
            // The by-index restore's exact flags: Once maps to IpcManual, no
            // Lock flag, so nothing of Poser's survives. This writes the
            // CAPTURED pre-import state back — never a revert to game state,
            // which on a shared clone/player identity would discard the
            // design the user actually had.
            int ec = _applyStateName.InvokeFunc(
                state, name, LockKey, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return ec is GlamourerEcSuccess or GlamourerEcNothingDone
                or GlamourerEcActorNotFound
                ? IntegrationResult.Ok()
                : GlamourerResult(ec, "restoring the captured state by name");
        });

    /// <summary>The shared preconditions of the two by-name calls: the
    /// availability gate, a real name, and the framework thread — the
    /// by-index guard normally supplies the last one through actor
    /// resolution, which by definition cannot run here.</summary>
    private IntegrationResult GuardedByName(
        string name, string what, Func<IntegrationResult> call) =>
        Guarded(Glamourer, what, () =>
        {
            if (!_actors.OnFrameworkThreadNow)
                return IntegrationResult.Fail(
                    "External integration calls must run on the framework thread.");
            return string.IsNullOrEmpty(name)
                ? IntegrationResult.Fail(
                    "No character name was captured for this import, so its Glamourer state cannot be addressed by name.")
                : call();
        });

    public IntegrationResult OpenGlamourer(ActorId actor)
    {
        // Force a fresh availability check at the click boundary.
        _availability.Expire();
        return Guarded(Glamourer, "Open in Glamourer", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            _openActorIndex.InvokeAction(index);
            return IntegrationResult.Ok();
        });
    }

    // ── the wardrobe ─────────────────────────────────────────────────────

    public IntegrationResult SetItem(ActorId actor, EquipSlot slot, ulong itemId, byte dye1, byte dye2) =>
        Guarded(Glamourer, "Set item", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            // A byte[] crosses Dalamud IPC as a base64 string and cannot become the
            // provider's IReadOnlyList<byte>; a List<byte> crosses as a JSON array.
            int ec = _setItem.InvokeFunc(index, (byte)slot, itemId, new List<byte> { dye1, dye2 }, 0u, ApplyOnce);
            return GlamourerResult(ec, "setting the item", actor);
        });

    public IntegrationResult SetFacewear(ActorId actor, ulong bonusItemId) =>
        Guarded(Glamourer, "Set facewear", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            const byte glasses = 1;
            int ec = _setBonusItem.InvokeFunc(index, glasses, bonusItemId, 0u, ApplyOnce);
            return GlamourerResult(ec, "setting the facewear", actor);
        });

    public IntegrationResult SetMetaSwitch(ActorId actor, MetaSwitch which, bool on) =>
        Guarded(Glamourer, "Set switch", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            int ec = _setMetaState.InvokeFunc(index, (ulong)which, on, 0u, ApplyOnce);
            return GlamourerResult(ec, "setting the switch", actor);
        });

    public GlamourerAccess ProbeGlamourerAccess(ActorId actor)
    {
        if (!Glamourer.Available)
            return new(GlamourerAccessKind.Unavailable, Glamourer.Detail);
        try
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return new(GlamourerAccessKind.Unavailable, detail);
            var (code, state) = _getState.InvokeFunc(index, 0u);
            if (code == GlamourerEcInvalidKey)
            {
                // Read only: a successful keyed retry proves our key is accepted,
                // not permission for ordinary edits to bypass an MCDF hold.
                var (keyedCode, keyedState) = _getState.InvokeFunc(index, LockKey);
                return ClassifyAccess(code, state is not null, keyedCode, keyedState is not null);
            }
            return ClassifyAccess(code, state is not null, null, false);
        }
        catch (Exception ex)
        {
            return new(GlamourerAccessKind.Unavailable, $"Read appearance access: {ex.Message}");
        }
    }

    internal static GlamourerAccess ClassifyAccess(int code, bool hasState, int? keyedCode, bool hasKeyedState)
    {
        if (code is GlamourerEcSuccess or GlamourerEcNothingDone && hasState)
            return GlamourerAccess.Editable;
        if (code == GlamourerEcInvalidKey)
        {
            if (keyedCode == GlamourerEcInvalidKey)
                return GlamourerAccess.ForeignHeld;
            if (keyedCode is GlamourerEcSuccess or GlamourerEcNothingDone && hasKeyedState)
                return GlamourerAccess.PoserHeld;
        }
        return new(GlamourerAccessKind.Unavailable, "Glamourer's appearance access could not be read.");
    }

    public IntegrationValue<string> GetGlamourerStateJson(ActorId actor) =>
        Guarded<string>(Glamourer, "Read state", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<string>.Fail(detail!);
            var (ec, state) = _getState.InvokeFunc(index, 0u);
            if (ec is not (GlamourerEcSuccess or GlamourerEcNothingDone) || state is null)
                return GlamourerReadFailure<string>(actor, ec);
            return IntegrationValue<string>.Ok(state.ToString(Newtonsoft.Json.Formatting.None));
        });

    public IntegrationValue<WardrobeState> GetWardrobeState(ActorId actor) =>
        Guarded<WardrobeState>(Glamourer, "Read wardrobe", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<WardrobeState>.Fail(detail!);
            var (ec, state) = _getState.InvokeFunc(index, 0u);
            if (ec is not (GlamourerEcSuccess or GlamourerEcNothingDone) || state is null)
                return GlamourerReadFailure<WardrobeState>(actor, ec);
            return IntegrationValue<WardrobeState>.Ok(ParseWardrobe(state));
        });

    public IntegrationValue<CustomizeState> GetCustomizeState(ActorId actor) =>
        Guarded<CustomizeState>(Glamourer, "Read look", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<CustomizeState>.Fail(detail!);
            var (ec, state) = _getState.InvokeFunc(index, 0u);
            if (ec is not (GlamourerEcSuccess or GlamourerEcNothingDone) || state is null)
                return GlamourerReadFailure<CustomizeState>(actor, ec);
            return IntegrationValue<CustomizeState>.Ok(ParseCustomize(state));
        });

    /// <summary>The customization out of the state JSON: every key under
    /// Customize as a plain number (a bool reads as 0 or 1).</summary>
    internal static CustomizeState ParseCustomize(Newtonsoft.Json.Linq.JObject state)
    {
        var values = new Dictionary<CustomizeKey, int>();
        int modelId = 0;
        if (state["Customize"] is Newtonsoft.Json.Linq.JObject customize)
        {
            modelId = customize["ModelId"]?.ToObject<int>() ?? 0;
            foreach (var key in Enum.GetValues<CustomizeKey>())
            {
                if (customize[key.ToString()]?["Value"] is not { } token)
                    continue;
                values[key] = token.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
                    ? (token.ToObject<bool>() ? 1 : 0)
                    : token.ToObject<int>();
            }
        }
        return new CustomizeState(values, modelId);
    }

    public IntegrationResult SetCustomize(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values) =>
        Guarded(Glamourer, "Set look", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            var (ec, state) = _getState.InvokeFunc(index, 0u);
            if (ec is not (GlamourerEcSuccess or GlamourerEcNothingDone) || state is null)
            {
                var failure = GlamourerReadFailure<CustomizeState>(actor, ec);
                return new(false, failure.Detail, failure.AppearanceRefusal);
            }
            var request = CustomizeRequest.Build(state, values);
            if (!request.Success || request.Value is null)
                return IntegrationResult.Fail(request.Detail ?? "The customization request is invalid.");
            // A string crosses as base64 to Glamourer; a JObject is read as JSON.
            int rc = _applyState.InvokeFunc(request.Value, index, 0u, ApplyOnce | ApplyCustomization);
            return GlamourerResult(rc, "setting the look", actor);
        });

    private static readonly (EquipSlot Slot, string Key)[] WardrobeSlotKeys =
    {
        (EquipSlot.MainHand, "MainHand"), (EquipSlot.OffHand, "OffHand"), (EquipSlot.Head, "Head"),
        (EquipSlot.Body, "Body"), (EquipSlot.Hands, "Hands"), (EquipSlot.Legs, "Legs"),
        (EquipSlot.Feet, "Feet"), (EquipSlot.Ears, "Ears"), (EquipSlot.Neck, "Neck"),
        (EquipSlot.Wrists, "Wrists"), (EquipSlot.RightFinger, "RFinger"), (EquipSlot.LeftFinger, "LFinger"),
    };

    /// <summary>The wardrobe out of Glamourer's state JSON: the twelve
    /// slots under Equipment with ItemId, Stain and Stain2; the switches
    /// under Hat, Visor, Weapon and VieraEars; the glasses under Bonus.</summary>
    internal static WardrobeState ParseWardrobe(Newtonsoft.Json.Linq.JObject state)
    {
        var equipment = state["Equipment"] as Newtonsoft.Json.Linq.JObject;
        var slots = new Dictionary<EquipSlot, WardrobeSlot>();
        foreach (var (slot, key) in WardrobeSlotKeys)
        {
            if (equipment?[key] is not Newtonsoft.Json.Linq.JObject worn)
                continue;
            slots[slot] = new WardrobeSlot(
                worn.Value<ulong?>("ItemId") ?? 0,
                worn.Value<byte?>("Stain") ?? 0,
                worn.Value<byte?>("Stain2") ?? 0);
        }
        bool Flag(string key, string field, bool fallback) =>
            equipment?[key]?[field]?.ToObject<bool>() ?? fallback;
        ulong facewear = state["Bonus"]?["Glasses"]?["BonusId"]?.ToObject<ulong>() ?? 0;
        return new WardrobeState(
            slots,
            facewear,
            Flag("Hat", "Show", true),
            Flag("Visor", "IsToggled", false),
            Flag("Weapon", "Show", true),
            Flag("VieraEars", "Show", true));
    }

    public IntegrationResult ApplyGlamourerStateJson(ActorId actor, string stateJson) =>
        Guarded(Glamourer, "Apply state", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            Newtonsoft.Json.Linq.JObject parsed;
            try
            {
                parsed = Newtonsoft.Json.Linq.JObject.Parse(stateJson);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return IntegrationResult.Fail($"The state is not JSON: {ex.Message}");
            }
            // A string crosses as base64 to Glamourer; a JObject is read as JSON.
            int ec = _applyState.InvokeFunc(parsed, index, 0u, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return GlamourerResult(ec, "applying the state", actor);
        });

    public IntegrationResult RevertGlamourerState(ActorId actor) =>
        Guarded(Glamourer, "Revert", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            int ec = _revertState.InvokeFunc(index, 0u, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return GlamourerResult(ec, "reverting the state", actor);
        });

    public IntegrationResult CopySpawnAppearance(nint sourceAddress, nint targetAddress)
    {
        if (!Glamourer.Available) return IntegrationResult.Ok();
        return Guarded(Glamourer, "Initialize duplicate appearance", () =>
        {
            if (_actors.AddressPair(sourceAddress, targetAddress) is { } refusal) return refusal;
            return CopySpawnAppearance(IntegrationActorResolution.IndexOf(sourceAddress), IntegrationActorResolution.IndexOf(targetAddress),
                _getState.InvokeFunc, () => ResetSpawnAppearance(targetAddress), _applyState.InvokeFunc);
        });
    }

    internal static IntegrationResult CopySpawnAppearance(int sourceIndex, int targetIndex,
        Func<int, uint, (int, Newtonsoft.Json.Linq.JObject?)> readState,
        Func<IntegrationResult> prepareTarget,
        Func<object, int, uint, ulong, int> applyState)
    {
        var (read, state) = readState(sourceIndex, 0u);
        // MCDF holds are ours only when Glamourer accepts our owner key.
        // Reading with that key does not unlock or edit the source. A foreign
        // hold still refuses, and the new target is always written unkeyed.
        if (read == GlamourerEcInvalidKey)
            (read, state) = readState(sourceIndex, LockKey);
        if (read == GlamourerEcInvalidKey)
            return IntegrationResult.Refused(GlamourerAccess.ForeignHeld);
        if (read is not (GlamourerEcSuccess or GlamourerEcNothingDone) || state == null)
            return IntegrationResult.Fail($"Could not read source appearance (code {read}).");
        var prepared = prepareTarget();
        if (!prepared.Success) return prepared;
        int applied = applyState(state.DeepClone(), targetIndex, 0u,
            ApplyOnce | ApplyEquipment | ApplyCustomization);
        return GlamourerResult(applied, "initializing the duplicate's appearance");
    }

    public IntegrationResult ResetSpawnAppearance(nint address)
    {
        // A missing optional provider has no retained state to clear.
        if (!Glamourer.Available) return IntegrationResult.Ok();
        return Guarded(Glamourer, "Initialize spawn appearance", () =>
        {
            if (_actors.AddressPair(address, address) is { } refusal) return refusal;
            if (_objects[IntegrationActorResolution.IndexOf(address)] is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter target
                || target.Address != address)
                return IntegrationResult.Fail("The owned spawn is no longer a player-kind object.");
            string name = target.Name.TextValue;
            // This API is name-based. Restrict it to the freshly self-identified
            // Poser body and refuse any live same-name actor, including its source.
            if (!name.StartsWith("Poser ", StringComparison.Ordinal)
                || _objects.Any(other => other.Address != address && other.Name.TextValue == name))
                return IntegrationResult.Fail("The owned spawn's appearance identity is not unique.");
            // Both BaseData and ModelData survive slot reuse. Revert restores the
            // stale baseline, and ApplyState can return Success while refusing a
            // non-human -> human change. Forget only this new body's unheld state;
            // the next read/draw initializes it from the native body we just seeded.
            int ec = _deletePlayerState.InvokeFunc(name, checked((ushort)target.HomeWorld.RowId), 0u);
            return GlamourerResult(ec, "initializing the spawned actor's appearance");
        });
    }

    public IntegrationValue<Guid> AddDesign(string stateJson, string name) =>
        Guarded<Guid>(Glamourer, "Save design", () =>
        {
            var (ec, id) = _addDesign.InvokeFunc(stateJson, name);
            return ec is GlamourerEcSuccess
                ? IntegrationValue<Guid>.Ok(id)
                : IntegrationValue<Guid>.Fail($"Glamourer failed saving the design (code {ec}).");
        });

    private IntegrationValue<T> GlamourerReadFailure<T>(ActorId actor, int code)
    {
        if (code != GlamourerEcInvalidKey)
            return IntegrationValue<T>.Fail($"Glamourer failed reading the state (code {code}).");
        var access = ProbeGlamourerAccess(actor);
        return access.CanEdit
            ? IntegrationValue<T>.Fail("Glamourer appearance access changed during the read; try again.")
            : IntegrationValue<T>.Refused(access);
    }

    private IntegrationResult GlamourerResult(int code, string what, ActorId actor)
    {
        if (code != GlamourerEcInvalidKey)
            return GlamourerResult(code, what);
        var access = ProbeGlamourerAccess(actor);
        return access.CanEdit
            ? IntegrationResult.Fail("Glamourer appearance access changed during the command; try again.")
            : IntegrationResult.Refused(access);
    }

    private static IntegrationResult GlamourerResult(int ec, string what) => ec switch
    {
        GlamourerEcSuccess or GlamourerEcNothingDone => IntegrationResult.Ok(),
        GlamourerEcInvalidKey => IntegrationResult.Refused(GlamourerAccess.ForeignHeld),
        _ => IntegrationResult.Fail($"Glamourer failed {what} (code {ec})."),
    };
}
