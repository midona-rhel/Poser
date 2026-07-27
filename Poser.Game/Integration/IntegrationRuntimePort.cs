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
using Poser.Services;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Integration;

/// <summary>
/// The one external-integration runtime boundary: version-gated raw call
/// gates for Penumbra, Glamourer, and Customize+ (no API packages — raw
/// Dalamud subscribers with enums as ints, matching the current providers).
/// Every actor-targeted call resolves the exact stable generation to an
/// object index HERE, at the call boundary; nothing native is retained.
///
/// Endpoint labels are pinned against Penumbra.Api 874a377 (breaking 5,
/// temporary collections at V6), Glamourer.Api 51c15bb (ApiVersion.V2 1.8+,
/// the verified floor carrying the Open* endpoints), and Customize+
/// 0f3dfba (API 6.x). Glamourer flag words: Once 0x1, Equipment 0x2,
/// Customization 0x4, Lock 0x8.
/// </summary>
public sealed class IntegrationRuntimePort : IIntegrationRuntimePort
{
    /// <summary>Poser's Glamourer lock key ("POSR"). Passing it on every
    /// state call succeeds on unlocked and Poser-locked states and fails
    /// with InvalidKey on states locked by other plugins — exactly the
    /// refusal the contract wants.</summary>
    private const uint LockKey = 0x504F5352;

    private const ulong ApplyOnce = 0x1;
    private const ulong ApplyEquipment = 0x2;
    private const ulong ApplyCustomization = 0x4;
    private const ulong ApplyLock = 0x8;

    private const int GlamourerEcSuccess = 0;
    private const int GlamourerEcNothingDone = 1;
    private const int GlamourerEcInvalidKey = 6;

    private const int PenumbraEcSuccess = 0;
    private const int PenumbraEcNothingChanged = 1;
    private const int PenumbraEcCollectionMissing = 2;

    private const int CustomizeEcSuccess = 0;
    private const int CustomizeEcInvalidCharacter = 1;
    private const int CustomizeEcProfileNotFound = 3;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IActorManager _actors;

    // Penumbra
    private readonly ICallGateSubscriber<(int Breaking, int Features)> _penumbraVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getCollections;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _getCollectionForObject;
    private readonly ICallGateSubscriber<int, Guid?, bool, bool, (int, (Guid, string)?)> _setCollectionForObject;
    private readonly ICallGateSubscriber<string, string, (int, Guid)> _createTemporaryCollection;
    private readonly ICallGateSubscriber<Guid, int> _deleteTemporaryCollection;
    private readonly ICallGateSubscriber<Guid, int, bool, int> _assignTemporaryCollection;
    private readonly ICallGateSubscriber<string, Guid, Dictionary<string, string>, string, int, int> _addTemporaryMod;
    private readonly ICallGateSubscriber<int, string> _getMetaManipulations;
    private readonly ICallGateSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]> _getResourcePaths;
    private readonly ICallGateSubscriber<string> _getModDirectory;
    private readonly ICallGateSubscriber<int, int, object?> _redrawObject;

    // Glamourer
    private readonly ICallGateSubscriber<(int Major, int Minor)> _glamourerVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getDesignList;
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> _applyDesign;
    private readonly ICallGateSubscriber<int, uint, (int, string?)> _getStateBase64;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _applyState;
    private readonly ICallGateSubscriber<int, uint, int> _unlockState;
    private readonly ICallGateSubscriber<int, object?> _openActorIndex;

    // Customize+
    private readonly ICallGateSubscriber<(int Breaking, int Feature)> _customizeVersion;
    private readonly ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>> _getProfileList;
    private readonly ICallGateSubscriber<Guid, (int, string?)> _getProfileByUniqueId;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> _getActiveProfileId;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> _setTemporaryProfile;
    private readonly ICallGateSubscriber<Guid, int> _deleteTemporaryProfileById;

    private DateTime _nextPenumbraCheck = DateTime.MinValue;
    private DateTime _nextGlamourerCheck = DateTime.MinValue;
    private DateTime _nextCustomizeCheck = DateTime.MinValue;
    private IntegrationAvailability _penumbra = new(false, "Penumbra has not been checked yet.");
    private IntegrationAvailability _glamourer = new(false, "Glamourer has not been checked yet.");
    private IntegrationAvailability _customize = new(false, "Customize+ has not been checked yet.");

    public IntegrationRuntimePort(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        StableBindingRegistry bindings,
        IActorManager actors)
    {
        _pluginInterface = pluginInterface;
        _framework = framework;
        _bindings = bindings;
        _actors = actors;

        _penumbraVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
        _getCollections = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5");
        _getCollectionForObject = pluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _setCollectionForObject = pluginInterface.GetIpcSubscriber<int, Guid?, bool, bool, (int, (Guid, string)?)>("Penumbra.SetCollectionForObject.V5");
        _createTemporaryCollection = pluginInterface.GetIpcSubscriber<string, string, (int, Guid)>("Penumbra.CreateTemporaryCollection.V6");
        _deleteTemporaryCollection = pluginInterface.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5");
        _assignTemporaryCollection = pluginInterface.GetIpcSubscriber<Guid, int, bool, int>("Penumbra.AssignTemporaryCollection.V5");
        _addTemporaryMod = pluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5");
        _getMetaManipulations = pluginInterface.GetIpcSubscriber<int, string>("Penumbra.GetMetaManipulations.V5");
        _getResourcePaths = pluginInterface.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5");
        _getModDirectory = pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        _redrawObject = pluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");

        _glamourerVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        _getDesignList = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2");
        _applyDesign = pluginInterface.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        _getStateBase64 = pluginInterface.GetIpcSubscriber<int, uint, (int, string?)>("Glamourer.GetStateBase64");
        _applyState = pluginInterface.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");
        _unlockState = pluginInterface.GetIpcSubscriber<int, uint, int>("Glamourer.UnlockState");
        _openActorIndex = pluginInterface.GetIpcSubscriber<int, object?>("Glamourer.OpenActorIndex");

        _customizeVersion = pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        _getProfileList = pluginInterface.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>("CustomizePlus.Profile.GetList");
        _getProfileByUniqueId = pluginInterface.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        _getActiveProfileId = pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        _setTemporaryProfile = pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        _deleteTemporaryProfileById = pluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
    }

    // ── Availability ─────────────────────────────────────────────────────

    public IntegrationAvailability Penumbra
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now >= _nextPenumbraCheck)
            {
                _nextPenumbraCheck = now + CheckInterval;
                _penumbra = Check("Penumbra", () =>
                {
                    var (breaking, _) = _penumbraVersion.InvokeFunc();
                    return breaking == 5
                        ? null
                        : $"Penumbra's API v{breaking} is not supported (needs v5).";
                });
            }
            return _penumbra;
        }
    }

    public IntegrationAvailability Glamourer
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now >= _nextGlamourerCheck)
            {
                _nextGlamourerCheck = now + CheckInterval;
                _glamourer = Check("Glamourer", () =>
                {
                    var (major, minor) = _glamourerVersion.InvokeFunc();
                    return major == 1 && minor >= 8
                        ? null
                        : $"Glamourer's API {major}.{minor} is not supported (needs 1.8).";
                });
            }
            return _glamourer;
        }
    }

    public IntegrationAvailability CustomizePlus
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now >= _nextCustomizeCheck)
            {
                _nextCustomizeCheck = now + CheckInterval;
                _customize = Check("CustomizePlus", () =>
                {
                    var (breaking, _) = _customizeVersion.InvokeFunc();
                    return breaking == 6
                        ? null
                        : $"Customize+'s API v{breaking} is not supported (needs v6).";
                }, displayName: "Customize+");
            }
            return _customize;
        }
    }

    private IntegrationAvailability Check(
        string internalName, Func<string?> versionGate, string? displayName = null)
    {
        var name = displayName ?? internalName;
        bool installed = _pluginInterface.InstalledPlugins.Any(
            plugin => plugin.InternalName == internalName && plugin.IsLoaded);
        if (!installed)
            return new IntegrationAvailability(false, $"{name} is not installed or not loaded.");
        try
        {
            return versionGate() is { } mismatch
                ? new IntegrationAvailability(false, mismatch)
                : new IntegrationAvailability(true, $"{name} is available.");
        }
        catch (Exception)
        {
            return new IntegrationAvailability(false, $"{name} is not responding.");
        }
    }

    // ── Actor resolution ─────────────────────────────────────────────────

    public Task<T> OnFrameworkThread<T>(Func<T> action) =>
        _framework.RunOnFrameworkThread(action);

    public bool IsResolvable(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return false;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success && resolved.Value is { } legacy && legacy.Address != nint.Zero;
    }

    private int ResolveIndex(ActorId actor, out string? detail)
    {
        detail = null;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            detail = "External integration calls must run on the framework thread.";
            return -1;
        }
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? "The actor is no longer available.";
            return -1;
        }
        return IndexOf(legacy.Address);
    }

    private static unsafe int IndexOf(nint address) =>
        ((CSGameObject*)address)->ObjectIndex;

    private static unsafe bool IsDrawable(nint address)
    {
        var native = (CSGameObject*)address;
        return native->RenderFlags == 0 && native->DrawObject != null;
    }

    // ── Penumbra ─────────────────────────────────────────────────────────

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetCollections() =>
        Guarded(Penumbra, "Collections", () =>
        {
            var collections = _getCollections.InvokeFunc();
            IReadOnlyList<ExternalItem> items = collections
                .Select(pair => new ExternalItem(pair.Key, pair.Value))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(items);
        });

    public IntegrationValue<CollectionAssignment> GetCollectionAssignment(ActorId actor) =>
        Guarded(Penumbra, "Collection", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<CollectionAssignment>.Fail(detail!);
            var (valid, individual, (id, name)) = _getCollectionForObject.InvokeFunc(index);
            return valid
                ? IntegrationValue<CollectionAssignment>.Ok(new CollectionAssignment(id, name, individual))
                : IntegrationValue<CollectionAssignment>.Fail("Penumbra cannot identify this actor.");
        });

    public IntegrationPortResult SetIndividualCollection(ActorId actor, Guid collection) =>
        Guarded(Penumbra, "Set collection", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            var (ec, _) = _setCollectionForObject.InvokeFunc(
                index, collection, /*allowCreateNew*/ true, /*allowDelete*/ false);
            return PenumbraResult(ec, "assigning the collection");
        });

    public IntegrationPortResult RestoreCollection(ActorId actor, CollectionBaseline baseline) =>
        Guarded(Penumbra, "Restore collection", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            // Restoring inheritance deletes Poser's individual assignment;
            // restoring an individual assignment puts the exact prior
            // collection back.
            var (ec, _) = baseline.HadIndividualAssignment
                ? _setCollectionForObject.InvokeFunc(
                    index, baseline.IndividualCollection, true, false)
                : _setCollectionForObject.InvokeFunc(index, null, false, true);
            return PenumbraResult(ec, "restoring the collection assignment");
        });

    public IntegrationValue<Guid> CreateTemporaryCollection(string name) =>
        Guarded(Penumbra, "Temporary collection", () =>
        {
            var (createEc, collection) = _createTemporaryCollection.InvokeFunc("Poser", name);
            return createEc == PenumbraEcSuccess
                ? IntegrationValue<Guid>.Ok(collection)
                : IntegrationValue<Guid>.Fail(
                    $"Penumbra failed creating the temporary collection (code {createEc}).");
        });

    public IntegrationPortResult AssignTemporaryCollection(Guid collection, ActorId actor) =>
        Guarded(Penumbra, "Assign temporary collection", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            // forceAssignment is REQUIRED for actors with an ordinary
            // individual assignment (force:false answers
            // CharacterCollectionExists) and is what lets the temporary
            // overlay that assignment while preserving it underneath. It
            // would also delete an existing temporary assignment — which
            // is why the session classifies the effective assignment in
            // the same framework action immediately before this call and
            // refuses foreign temporaries there; nothing can interleave.
            int assignEc = _assignTemporaryCollection.InvokeFunc(
                collection, index, /*forceAssignment*/ true);
            return assignEc == PenumbraEcSuccess
                ? IntegrationPortResult.Ok()
                : IntegrationPortResult.Fail(
                    $"Penumbra failed assigning the temporary collection (code {assignEc}).");
        });

    public IntegrationPortResult AddTemporaryMods(
        Guid collection, IReadOnlyDictionary<string, string> paths, string manipulations) =>
        Guarded(Penumbra, "Temporary mods", () =>
        {
            int ec = _addTemporaryMod.InvokeFunc(
                "PoserMCDF",
                collection,
                paths.ToDictionary(pair => pair.Key, pair => pair.Value),
                manipulations,
                0);
            return PenumbraResult(ec, "adding the temporary mod");
        });

    public IntegrationPortResult DeleteTemporaryCollection(Guid collection) =>
        Guarded(Penumbra, "Delete temporary collection", () =>
        {
            int ec = _deleteTemporaryCollection.InvokeFunc(collection);
            // An already-absent collection (CollectionMissing) is an
            // idempotent cleanup success, like Customize+ ProfileNotFound
            // and Glamourer NothingDone.
            return ec is PenumbraEcSuccess or PenumbraEcNothingChanged
                    or PenumbraEcCollectionMissing
                ? IntegrationPortResult.Ok()
                : IntegrationPortResult.Fail(
                    $"Penumbra failed deleting the temporary collection (code {ec}).");
        });

    public IntegrationValue<string> GetActorMetaManipulations(ActorId actor) =>
        Guarded(Penumbra, "Meta manipulations", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<string>.Fail(detail!);
            return IntegrationValue<string>.Ok(_getMetaManipulations.InvokeFunc(index));
        });

    public IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetActorResourcePaths(ActorId actor) =>
        Guarded(Penumbra, "Resource paths", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Fail(detail!);
            var trees = _getResourcePaths.InvokeFunc(new[] { (ushort)index });
            if (trees.Length == 0 || trees[0] is not { } tree)
                return IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Fail(
                    "Penumbra reported no resources for this actor.");
            IReadOnlyDictionary<string, IReadOnlyList<string>> mapped = tree.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToList());
            return IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Ok(mapped);
        });

    public IntegrationValue<string> GetModDirectory() =>
        Guarded(Penumbra, "Mod directory", () =>
            IntegrationValue<string>.Ok(_getModDirectory.InvokeFunc()));

    public IntegrationPortResult RequestRedraw(ActorId actor) =>
        Guarded(Penumbra, "Redraw", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            _redrawObject.InvokeAction(index, 0);
            return IntegrationPortResult.Ok();
        });

    public async Task<IntegrationPortResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation)
    {
        var requested = await OnFrameworkThread(() => RequestRedraw(actor));
        if (!requested.Success)
            return requested;

        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        // Give the redraw a moment to actually tear the draw object down,
        // or the first poll can see the old body still "drawable".
        await Task.Delay(150, CancellationToken.None);
        while (true)
        {
            if (cancellation.IsCancellationRequested)
                return IntegrationPortResult.Fail("The operation was cancelled.");
            var state = await OnFrameworkThread(() =>
            {
                var resolved = _bindings.Resolve(actor);
                if (!resolved.Success || resolved.Value is not { } legacy
                    || legacy.Address == nint.Zero)
                    return (Gone: true, Drawable: false);
                return (Gone: false, Drawable: IsDrawable(legacy.Address));
            });
            if (state.Gone)
                return IntegrationPortResult.Fail(
                    "The actor disappeared while waiting for its redraw.");
            if (state.Drawable)
            {
                // Rebuild bindings against the redrawn body so downstream
                // exact-generation state reconciles before anything else
                // touches the actor.
                await OnFrameworkThread(() =>
                {
                    _actors.RefreshActors();
                    return true;
                });
                return IntegrationPortResult.Ok();
            }
            if (Environment.TickCount64 > deadline)
                return IntegrationPortResult.Fail(
                    $"The actor did not finish redrawing within {timeout.TotalSeconds:0} seconds.");
            await Task.Delay(100, CancellationToken.None);
        }
    }

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
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<string>.Fail(detail!);
            var (ec, state) = _getStateBase64.InvokeFunc(index, 0u);
            if (ec == GlamourerEcInvalidKey)
                return IntegrationValue<string>.Fail(
                    "This actor's Glamourer state is locked by another plugin.");
            if (ec != GlamourerEcSuccess || state == null)
                return IntegrationValue<string>.Fail(
                    $"Glamourer failed reading the actor state (code {ec}).");
            return IntegrationValue<string>.Ok(state);
        });

    public IntegrationPortResult ApplyDesign(ActorId actor, Guid design) =>
        Guarded(Glamourer, "Apply design", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            // The API's documented design default: Once | Equipment |
            // Customization — applied once, no persistent lock.
            int ec = _applyDesign.InvokeFunc(
                design, index, 0u, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return GlamourerResult(ec, "applying the design");
        });

    public IntegrationPortResult HoldGlamourerState(ActorId actor, string state) =>
        Guarded(Glamourer, "Hold state", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            // Fixed + locked: without Once the state maps to IpcFixed, and
            // the Lock flag with Poser's key keeps automation off the
            // imported look until UnlockGlamourerState releases it.
            int ec = _applyState.InvokeFunc(
                state, index, LockKey, ApplyEquipment | ApplyCustomization | ApplyLock);
            return GlamourerResult(ec, "holding the actor state");
        });

    public IntegrationPortResult RestoreGlamourerState(ActorId actor, string state) =>
        Guarded(Glamourer, "Restore state", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            // One-shot manual restoration: Once maps to IpcManual, no Lock
            // flag — after a restore no Poser fixed state or lock remains.
            int ec = _applyState.InvokeFunc(
                state, index, LockKey, ApplyOnce | ApplyEquipment | ApplyCustomization);
            return GlamourerResult(ec, "restoring the actor state");
        });

    public IntegrationPortResult UnlockGlamourerState(ActorId actor) =>
        Guarded(Glamourer, "Unlock", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            int ec = _unlockState.InvokeFunc(index, LockKey);
            return ec is GlamourerEcSuccess or GlamourerEcNothingDone
                ? IntegrationPortResult.Ok()
                : GlamourerResult(ec, "releasing Poser's lock");
        });

    public IntegrationPortResult OpenGlamourer(ActorId actor)
    {
        // Force a fresh availability check at the click boundary.
        _nextGlamourerCheck = DateTime.MinValue;
        return Guarded(Glamourer, "Open in Glamourer", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationPortResult.Fail(detail!);
            _openActorIndex.InvokeAction(index);
            return IntegrationPortResult.Ok();
        });
    }

    // ── Customize+ ───────────────────────────────────────────────────────

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

    public IntegrationValue<BodyProfileProbe> ProbeBodyProfile(ActorId actor) =>
        Guarded(CustomizePlus, "Profile probe", () =>
        {
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<BodyProfileProbe>.Fail(detail!);
            var (ec, active) = _getActiveProfileId.InvokeFunc((ushort)index);
            if (ec == CustomizeEcProfileNotFound || active is not { } profile)
                return IntegrationValue<BodyProfileProbe>.Ok(new BodyProfileProbe(null, false));
            if (ec != CustomizeEcSuccess)
                return IntegrationValue<BodyProfileProbe>.Fail(
                    $"Customize+ failed reading the active profile (code {ec}).");
            // Saved profiles answer GetByUniqueId; a temporary profile is
            // reported active but cannot be read back.
            var (readEc, _) = _getProfileByUniqueId.InvokeFunc(profile);
            return IntegrationValue<BodyProfileProbe>.Ok(
                new BodyProfileProbe(profile, readEc == CustomizeEcSuccess));
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
            int index = ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<Guid>.Fail(detail!);
            var (ec, created) = _setTemporaryProfile.InvokeFunc((ushort)index, profileJson);
            return ec == CustomizeEcSuccess && created is { } id
                ? IntegrationValue<Guid>.Ok(id)
                : IntegrationValue<Guid>.Fail(
                    $"Customize+ failed applying the temporary profile (code {ec}).");
        });

    public IntegrationPortResult DeleteTemporaryBodyProfileById(Guid profile) =>
        Guarded(CustomizePlus, "Delete profile", () =>
        {
            int ec = _deleteTemporaryProfileById.InvokeFunc(profile);
            // Already-absent profile and already-gone owning actor are both
            // successful releases (Customize+ itself documents
            // InvalidCharacter on this path as "not an error").
            return ec is CustomizeEcSuccess or CustomizeEcProfileNotFound
                    or CustomizeEcInvalidCharacter
                ? IntegrationPortResult.Ok()
                : IntegrationPortResult.Fail(
                    $"Customize+ failed deleting the temporary profile (code {ec}).");
        });

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IntegrationPortResult Guarded(
        IntegrationAvailability availability, string what, Func<IntegrationPortResult> call)
    {
        if (!availability.Available)
            return IntegrationPortResult.Fail(availability.Detail);
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return IntegrationPortResult.Fail($"{what}: {ex.Message}");
        }
    }

    private static IntegrationValue<T> Guarded<T>(
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

    private static IntegrationPortResult PenumbraResult(int ec, string what) =>
        ec is PenumbraEcSuccess or PenumbraEcNothingChanged
            ? IntegrationPortResult.Ok()
            : IntegrationPortResult.Fail($"Penumbra failed {what} (code {ec}).");

    private static IntegrationPortResult GlamourerResult(int ec, string what) => ec switch
    {
        GlamourerEcSuccess or GlamourerEcNothingDone => IntegrationPortResult.Ok(),
        GlamourerEcInvalidKey => IntegrationPortResult.Fail(
            "This actor's Glamourer state is locked by another plugin."),
        _ => IntegrationPortResult.Fail($"Glamourer failed {what} (code {ec})."),
    };
}
