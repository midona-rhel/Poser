using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Domain.Companions;
using Poser.Game.Integration;
using Poser.Domain.Identity;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game;

/// <summary>
/// Service for spawning and destroying actors in GPose.
/// Based on Brio's ActorSpawnService implementation.
///
/// <para>The facade owns the thread and spawn-authority gates; every
/// collaborator behind it (<see cref="SpawnTransaction"/>,
/// <see cref="ActorRemoval"/>, <see cref="ActorCompanionControl"/>,
/// <see cref="ActorModelControl"/>) runs only after both were proven, against
/// the one <see cref="SpawnOwnershipLedger"/>.</para>
/// </summary>
public unsafe class ActorSpawnService : IActorSpawnService
{
    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog? _log;
    private readonly IFramework? _framework;
    private readonly Func<nint> _localPlayerAddress;
    private readonly bool _ownsAdapter;

    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnOwnershipLedger _ownership = new();
    private readonly SpawnActorResolver _resolver;
    private readonly SpawnOwnershipCleanup _cleanup;
    private readonly SpawnFramePoller _poller;
    private readonly SpawnTransaction _transaction;
    private readonly ActorRemoval _removal;
    private readonly ActorCompanionControl _companions;
    private readonly ActorModelControl _model;

    private bool _spawnUnavailableLogged;

    internal IReadOnlyList<SpawnOwnershipRecord> OwnershipSnapshot =>
        _ownership.Snapshot;

    public ActorSpawnService(
        IClientState clientState,
        IObjectTable objectTable,
        IGPoseService gPoseService,
        IActorManager actorManager,
        IEventBus eventBus,
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        ISpawnCollectionPort collections,
        ISpawnAppearancePort spawnAppearance)
        : this(
            gPoseService,
            actorManager,
            eventBus,
            new ActorSpawnNativeAdapter(sigScanner, hooking, log),
            () => objectTable.GetObjectAddress(0),
            log,
            framework,
            null,
            address => ExpectedWrapperIdentity(objectTable, address),
            null,
            collections,
            ownsAdapter: true,
            objectAddressAt: objectTable.GetObjectAddress,
            spawnAppearance: spawnAppearance)
    {
    }

    internal ActorSpawnService(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IEventBus eventBus,
        IActorSpawnNativeAdapter native,
        Func<nint> localPlayerAddress,
        IPluginLog? log = null,
        IFramework? framework = null,
        Action<SpawnOwnershipRecord, nint, int, string?>? applySpawnMutations = null,
        Func<nint, EntityId?>? expectedWrapperIdentity = null,
        Func<long>? clock = null,
        ISpawnCollectionPort? collections = null,
        bool ownsAdapter = false,
        Func<int, nint>? objectAddressAt = null,
        ISpawnAppearancePort? spawnAppearance = null)
    {
        _framework = framework;
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _log = log;
        _native = native;
        _localPlayerAddress = localPlayerAddress;
        _ownsAdapter = ownsAdapter;
        // Fail closed here too: with no way to read the object table, no
        // address is inside the GPose range and nothing pre-existing deletes.
        var addressAt = objectAddressAt ?? (_ => nint.Zero);
        // Fail closed: without a way to derive the expected wrapper identity,
        // no bind can be proven and spawn rolls back.
        var wrapperIdentity = expectedWrapperIdentity ?? (_ => null);
        var ticks = clock ?? (() => System.Environment.TickCount64);

        _resolver = new SpawnActorResolver(native, _ownership);
        _cleanup = new SpawnOwnershipCleanup(_ownership, native, collections, log);
        _poller = new SpawnFramePoller(framework, native, _ownership, ticks, log);
        _transaction = new SpawnTransaction(
            gPoseService, actorManager, native, _ownership, _cleanup, _poller,
            framework, collections, spawnAppearance, applySpawnMutations,
            wrapperIdentity, ticks, log);
        _removal = new ActorRemoval(
            actorManager, native, _ownership, _cleanup, wrapperIdentity, addressAt, log);
        _companions = new ActorCompanionControl(actorManager, native, _resolver, _poller, log);
        _model = new ActorModelControl(
            actorManager, eventBus, native, _ownership, _resolver, _poller, log);

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    /// <summary>Mints the identity ActorManager.RefreshActorsCore gives every
    /// GPose wrapper; the comparison must use the production formula, not a
    /// re-derivation from native ids.</summary>
    private static EntityId? ExpectedWrapperIdentity(IObjectTable objectTable, nint address)
    {
        var gameObject = objectTable.CreateObjectReference(address);
        // (EntityId?)null, not the bare literal: EntityId's implicit string
        // conversion would otherwise capture the null arm and produce a
        // non-null EntityId(null), silently defeating the fail-closed check.
        return gameObject is null
            ? (EntityId?)null
            : ActorManager.ActorIdentity.For(gameObject);
    }

    /// <summary>All native access happens on the framework (main) thread;
    /// off-thread calls refuse rather than race the game.</summary>
    private bool OnOwnerThread => _framework is null || _framework.IsInFrameworkUpdateThread;

    /// <summary>Spawning requires the authoritative lifetime transition; a
    /// record that cannot observe external destruction must not exist.</summary>
    private bool SpawnAuthorityAvailable()
    {
        if (_native.IsLifetimeAuthoritative)
            return true;
        if (!_spawnUnavailableLogged)
        {
            _log?.Warning(
                $"ActorSpawnService: spawning unavailable - {_native.LifetimeAuthorityDetail ?? "no authoritative lifetime"}");
            _spawnUnavailableLogged = true;
        }
        return false;
    }

    public IActor? SpawnNewActor(bool reserveCompanionSlot, int modelCharaId = 0) =>
        SpawnNewActor(reserveCompanionSlot, modelCharaId, out _);

    public IActor? SpawnNewActor(bool reserveCompanionSlot, int modelCharaId, out string? refusal)
    {
        if (!OnOwnerThread)
        {
            refusal = "Actors can only be spawned on the framework thread.";
            return null;
        }
        if (!SpawnAuthorityAvailable())
        {
            refusal = "Spawning is unavailable on this client: " +
                $"{_native.LifetimeAuthorityDetail ?? "no authoritative actor lifetime"}.";
            return null;
        }
        // Creation semantics, clone mechanism: like Brio, a NEW actor is
        // seeded from the local player's appearance.
        var localPlayer = _localPlayerAddress();
        if (localPlayer == nint.Zero)
        {
            _log?.Warning("ActorSpawnService: Cannot spawn - no local player");
            refusal = "There is no local player to seed the actor from.";
            return null;
        }
        // A new actor is not a copy of the player as far as mods go: it
        // wears the player's collection live, not a snapshot of it.
        return _transaction.SpawnCloneFrom(localPlayer, reserveCompanionSlot, out refusal, inheritSource: false,
            modelCharaId: modelCharaId);
    }

    public IActor? CloneActor(IActor source)
    {
        if (!OnOwnerThread || !SpawnAuthorityAvailable())
            return null;
        if (source.Address == nint.Zero)
        {
            _log?.Warning("ActorSpawnService: Cannot clone - source has no address");
            return null;
        }
        // The seed copy reads the clone source raw, so the source wrapper
        // must prove its identity through the adapter first. A stale wrapper
        // (null or faulting resolution) is refusal, never permission to
        // dereference its remembered address.
        SpawnNativeDescriptor? resolvedSource;
        try
        {
            resolvedSource = _native.ResolveActor(source.Address);
        }
        catch
        {
            resolvedSource = null;
        }
        if (resolvedSource is null)
        {
            _log?.Warning(
                "ActorSpawnService: Cannot clone - source identity did not resolve (stale wrapper)");
            return null;
        }
        // A clone keeps the slot so companion attachment stays possible,
        // matching the pre-split behavior of every Poser spawn.
        return _transaction.SpawnCloneFrom(resolvedSource.Value.Address, reserveCompanionSlot: true, out _);
    }

    /// <summary>Brio's AddFromWorld: the overworld character ITSELF joins
    /// GPose — the same body, no copy — and the scene lists it by
    /// reference for as long as GPose lasts. Nothing is written to it
    /// here; the registration is the game's own call.</summary>

    internal IActor? AdoptFromWorldSource(nint sourceAddress)
    {
        if (!OnOwnerThread || sourceAddress == nint.Zero || !_gPoseService.IsGPosing)
            return null;
        _transaction.AddCharacterToGPose((Character*)sourceAddress);
        _actorManager.AdoptWorldActor(sourceAddress);
        foreach (var actor in _actorManager.Actors)
            if (actor.Address == sourceAddress)
                return actor;
        return null;
    }

    public IActor? SpawnCatalogActor(SpawnCatalogEntry entry)
    {
        if (!OnOwnerThread || !SpawnAuthorityAvailable())
            return null;
        var localPlayer = _localPlayerAddress();
        if (localPlayer == nint.Zero)
        {
            _log?.Warning("ActorSpawnService: Cannot spawn - no local player");
            return null;
        }
        // No companion slot: the entry IS the actor, not something an owner
        // carries in a slot.
        var actor = _transaction.SpawnCloneFrom(
            localPlayer,
            reserveCompanionSlot: false,
            out _,
            inheritSource: false,
            modelCharaId: entry.ModelCharaId,
            // The game name stays a Poser slot name: Penumbra identifies a
            // player-kind object by a two-word letters-only name (each part
            // capitalized, no digits) and answered InvalidIdentifier (16) for
            // "Morbol seedling", so the actor got no collection (2026-09-03).
            // SpawnNames.ForSlot keeps every slot inside that rule (#427).
            // The label is the nickname instead.
            name: null,
            kind: entry.Kind);
        return actor;
    }

    public CompanionKind? GetSpawnedKind(IActor actor) =>
        OnOwnerThread ? _resolver.SpawnedKind(actor) : null;

    public bool IsSpawnedActor(IActor actor) =>
        OnOwnerThread && _resolver.IsSpawned(actor);

    public bool DestroyActor(IActor actor) =>
        OnOwnerThread && _removal.DestroyActor(actor);

    public bool RemoveActorFromScene(IActor actor) =>
        OnOwnerThread && _removal.RemoveActorFromScene(actor);

    public string? RemovalRefusal(IActor actor) => _removal.RemovalRefusal(actor);

    public bool CopyDrawnAppearance(IActor source, IActor target) =>
        OnOwnerThread && _model.CopyDrawnAppearance(source, target);

    public bool CopyEquipmentVisibility(IActor source, IActor target) =>
        OnOwnerThread && _model.CopyEquipmentVisibility(source, target);

    public void SetVisibility(IActor actor, bool visible)
    {
        if (OnOwnerThread)
            _model.SetVisibility(actor, visible);
    }

    public bool IsVisible(IActor actor) =>
        OnOwnerThread && _model.IsVisible(actor);

    public int GetModelCharaId(IActor actor) =>
        OnOwnerThread ? _model.GetModelCharaId(actor) : 0;

    public void SetModelCharaId(IActor actor, int modelCharaId)
    {
        if (OnOwnerThread)
            _model.SetModelCharaId(actor, modelCharaId);
    }

    public bool SetCompanion(IActor owner, CompanionAttachment? container) =>
        OnOwnerThread && _companions.SetCompanion(owner, container);

    public CompanionAttachment? GetCompanionInfo(IActor owner) =>
        OnOwnerThread ? _companions.GetCompanionInfo(owner) : null;

    public IActor? GetCompanionActor(IActor owner) =>
        OnOwnerThread ? _companions.GetCompanionActor(owner) : null;

    public bool HasCompanionSlot(IActor actor) =>
        OnOwnerThread && _companions.HasCompanionSlot(actor);

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            // Destroy all spawned actors when exiting GPose
            DestroyAllSpawned();
        }
    }

    private void DestroyAllSpawned()
    {
        _log?.Debug("ActorSpawnService: Destroying all spawned actors");

        var deleted = _cleanup.DeleteAll();

        // Both callers (GPose exit, dispose) end the session the legacy
        // visibility overrides belong to.
        _model.ClearSessionVisibility();

        if (deleted)
            _actorManager.RefreshActors();
    }

    public void Dispose()
    {
        _poller.Stop();
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        if (OnOwnerThread)
            DestroyAllSpawned();
        else
            _log?.Warning(
                "ActorSpawnService: disposed off the framework thread; native cleanup skipped (fail closed)");
        if (_ownsAdapter && _native is IDisposable disposable)
            disposable.Dispose();
    }
}
