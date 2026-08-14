using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;
using Poser.Entities;
using Poser.Game.Types;
using Poser.Services;

namespace Poser.Game;

internal readonly record struct SpawnNativeDescriptor(
    ushort Index,
    nint Address,
    ulong EntityId,
    ulong Generation = 0);

internal enum SpawnOwnershipState
{
    PendingCreate,
    Live,
    PendingDelete,
}

internal sealed class SpawnOwnershipRecord
{
    public SpawnOwnershipRecord(
        Guid token,
        ushort createdIndex,
        SpawnNativeDescriptor? descriptor,
        CompanionKind kind,
        bool hasCompanionSlot)
    {
        Token = token;
        CreatedIndex = createdIndex;
        Descriptor = descriptor;
        Kind = kind;
        HasCompanionSlot = hasCompanionSlot;
    }

    public Guid Token { get; }
    public ushort CreatedIndex { get; }
    public SpawnNativeDescriptor? Descriptor { get; private set; }
    public IActor? Actor { get; private set; }
    public CompanionKind Kind { get; }
    public bool HasCompanionSlot { get; }
    public bool Visible { get; private set; } = true;
    public SpawnOwnershipState State { get; private set; } = SpawnOwnershipState.PendingCreate;

    public void Resolve(SpawnNativeDescriptor descriptor)
    {
        if (descriptor.Index != CreatedIndex)
            throw new InvalidOperationException("Spawned object index changed");
        Descriptor = descriptor;
        State = SpawnOwnershipState.Live;
    }

    public void Bind(IActor actor) => Actor = actor;
    public void MarkPending() => State = SpawnOwnershipState.PendingDelete;
    public void SetVisibility(bool visible) => Visible = visible;
}

internal sealed class SpawnOwnershipLedger
{
    private readonly Dictionary<Guid, SpawnOwnershipRecord> _records = new();

    public IReadOnlyList<SpawnOwnershipRecord> Snapshot => _records.Values.ToArray();

    public SpawnOwnershipRecord Add(
        SpawnNativeDescriptor descriptor,
        CompanionKind kind,
        bool hasCompanionSlot)
    {
        var record = new SpawnOwnershipRecord(
            Guid.NewGuid(),
            descriptor.Index,
            descriptor,
            kind,
            hasCompanionSlot);
        _records.Add(record.Token, record);
        record.Resolve(descriptor);
        return record;
    }

    public SpawnOwnershipRecord AddPending(
        ushort index,
        CompanionKind kind,
        bool hasCompanionSlot)
    {
        var record = new SpawnOwnershipRecord(
            Guid.NewGuid(),
            index,
            null,
            kind,
            hasCompanionSlot);
        _records.Add(record.Token, record);
        return record;
    }

    public SpawnOwnershipRecord AddUnresolved(
        CompanionKind kind,
        bool hasCompanionSlot)
    {
        var record = new SpawnOwnershipRecord(
            Guid.NewGuid(),
            ushort.MaxValue,
            null,
            kind,
            hasCompanionSlot);
        _records.Add(record.Token, record);
        return record;
    }

    public bool Bind(Guid token, IActor actor)
    {
        return _records.TryGetValue(token, out var record)
            && record.Descriptor is { Address: var address }
            && address == actor.Address
            && (record.Actor is null || ReferenceEquals(record.Actor, actor))
            && BindRecord(record, actor);
    }

    private static bool BindRecord(SpawnOwnershipRecord record, IActor actor)
    {
        record.Bind(actor);
        return true;
    }

    public bool TryGetBound(IActor actor, out SpawnOwnershipRecord record)
    {
        record = _records.Values.FirstOrDefault(candidate =>
            candidate.State != SpawnOwnershipState.PendingCreate
            && candidate.Descriptor is not null
            && (candidate.Actor is not null
                ? ReferenceEquals(candidate.Actor, actor)
                : candidate.Descriptor.Value.Address == actor.Address))!;
        return record is not null;
    }

    public bool TryGetExact(
        IActor actor,
        SpawnNativeDescriptor descriptor,
        out SpawnOwnershipRecord record)
    {
        record = _records.Values.FirstOrDefault(candidate =>
            candidate.State == SpawnOwnershipState.Live
            && candidate.Descriptor == descriptor
            && candidate.Descriptor.Value.Address == actor.Address
            && (candidate.Actor is null || ReferenceEquals(candidate.Actor, actor)))!;
        return record is not null;
    }

    public CompanionKind GetKind(IActor actor, SpawnNativeDescriptor descriptor) =>
        TryGetExact(actor, descriptor, out var record)
            ? record.Kind
            : CompanionKind.None;

    public bool TrySetVisibility(
        IActor actor,
        SpawnNativeDescriptor descriptor,
        bool visible)
    {
        if (!TryGetExact(actor, descriptor, out var record))
            return false;
        record.SetVisibility(visible);
        return true;
    }

    public bool TryRetire(Guid token, SpawnNativeDescriptor? descriptor)
    {
        if (!_records.TryGetValue(token, out var record)
            || descriptor is null
            || record.Descriptor != descriptor.Value)
            return false;
        _records.Remove(token);
        return true;
    }

    public bool TryRetire(SpawnOwnershipRecord record) =>
        record.Descriptor is { } descriptor
            && TryRetire(record.Token, descriptor);

    public bool TryGetExact(
        Guid token,
        SpawnNativeDescriptor descriptor,
        out SpawnOwnershipRecord record)
    {
        record = _records.TryGetValue(token, out var candidate)
            && candidate.State == SpawnOwnershipState.Live
            && candidate.Descriptor == descriptor
            ? candidate
            : null!;
        return record is not null;
    }

    public bool MarkPending(Guid token)
    {
        if (!_records.TryGetValue(token, out var record))
            return false;
        record.MarkPending();
        return true;
    }
}

internal interface IActorSpawnNativeAdapter
{
    bool IsAvailable { get; }
    uint CreateBattleCharacter(byte reserveCompanionSlot);
    SpawnNativeDescriptor? ResolveByIndex(ushort index);
    SpawnNativeDescriptor? ResolveActor(nint address);
    bool DeleteExact(SpawnNativeDescriptor descriptor);
}

internal unsafe sealed class ActorSpawnNativeAdapter : IActorSpawnNativeAdapter
{
    private readonly Dictionary<ushort, (nint Address, ulong EntityId)> _lastSlots = new();
    private readonly Dictionary<ushort, ulong> _slotGenerations = new();

    public bool IsAvailable => ClientObjectManager.Instance() is not null;

    public uint CreateBattleCharacter(byte reserveCompanionSlot)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return 0xFFFFFFFF;
        return com->CreateBattleCharacter(reserveCompanionSlot);
    }

    public SpawnNativeDescriptor? ResolveByIndex(ushort index)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return null;
        var native = com->GetObjectByIndex(index);
        if (native is null)
        {
            _lastSlots.Remove(index);
            _slotGenerations[index] =
                _slotGenerations.GetValueOrDefault(index) + 1;
            return null;
        }

        var identity = ((nint)native, native->EntityId);
        if (!_lastSlots.TryGetValue(index, out var previous)
            || previous != identity)
        {
            _lastSlots[index] = identity;
            _slotGenerations[index] =
                _slotGenerations.GetValueOrDefault(index) + 1;
        }

        return new SpawnNativeDescriptor(
            native->ObjectIndex,
            (nint)native,
            native->EntityId,
            _slotGenerations[index]);
    }

    public SpawnNativeDescriptor? ResolveActor(nint address)
    {
        if (address == nint.Zero)
            return null;
        var com = ClientObjectManager.Instance();
        if (com is null)
            return null;
        var native = (GameObject*)address;
        var index = com->GetIndexByObject(native);
        if (index == 0xFFFFFFFF)
            return null;
        var current = ResolveByIndex((ushort)index);
        return current is { } descriptor && descriptor.Address == address
            ? descriptor
            : null;
    }

    public bool DeleteExact(SpawnNativeDescriptor descriptor)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return false;
        var current = ResolveByIndex(descriptor.Index);
        if (current is null || current.Value != descriptor)
            return false;
        com->DeleteObjectByIndex(descriptor.Index, 0);
        return true;
    }
}

internal static class SpawnOwnershipCleanup
{
    public static bool TryDelete(
        SpawnOwnershipLedger ledger,
        IActorSpawnNativeAdapter native,
        SpawnOwnershipRecord ownership)
    {
        try
        {
            if (ownership.Descriptor is null)
                return false;
            ledger.MarkPending(ownership.Token);
            if (!native.IsAvailable)
                return false;

            var current = native.ResolveByIndex(ownership.Descriptor.Value.Index);
            if (current is null)
                return ledger.TryRetire(ownership);
            if (current.Value != ownership.Descriptor.Value)
                return false;
            if (!native.DeleteExact(ownership.Descriptor.Value))
                return false;
            return ledger.TryRetire(ownership);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Service for spawning and destroying actors in GPose.
/// Based on Brio's ActorSpawnService implementation.
/// </summary>
public unsafe class ActorSpawnService : IActorSpawnService
{
    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog? _log;
    private readonly IFramework? _framework;
    private readonly Func<nint> _localPlayerAddress;

    private readonly IActorSpawnNativeAdapter _native;
    private readonly Action<SpawnOwnershipRecord, nint, int, string?> _applySpawnMutations;
    private readonly SpawnOwnershipLedger _ownership = new();
    private bool _disposed;

    internal IReadOnlyList<SpawnOwnershipRecord> OwnershipSnapshot =>
        _ownership.Snapshot;

    public ActorSpawnService(
        IClientState clientState,
        IObjectTable objectTable,
        IGPoseService gPoseService,
        IActorManager actorManager,
        IEventBus eventBus,
        IPluginLog log,
        IFramework framework)
        : this(
            gPoseService,
            actorManager,
            eventBus,
            new ActorSpawnNativeAdapter(),
            () => objectTable.GetObjectAddress(0),
            log,
            framework,
            null)
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
        Action<SpawnOwnershipRecord, nint, int, string?>? applySpawnMutations = null)
    {
        _framework = framework;
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _log = log;
        _native = native;
        _localPlayerAddress = localPlayerAddress;
        _applySpawnMutations = applySpawnMutations ?? ApplySpawnMutations;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public IActor? SpawnNewActor(bool reserveCompanionSlot)
    {
        // Creation semantics, clone mechanism: like Brio, a NEW actor is
        // seeded from the local player's appearance.
        var localPlayer = _localPlayerAddress();
        if (localPlayer == nint.Zero)
        {
            _log?.Warning("ActorSpawnService: Cannot spawn - no local player");
            return null;
        }
        return SpawnCloneFrom(localPlayer, reserveCompanionSlot);
    }

    public IActor? CloneActor(IActor source)
    {
        if (source.Address == nint.Zero)
        {
            _log?.Warning("ActorSpawnService: Cannot clone - source has no address");
            return null;
        }
        // A clone keeps the slot so companion attachment stays possible,
        // matching the pre-split behavior of every Poser spawn.
        return SpawnCloneFrom(source.Address, reserveCompanionSlot: true);
    }

    public IActor? SpawnCatalogActor(SpawnCatalogEntry entry)
    {
        var localPlayer = _localPlayerAddress();
        if (localPlayer == nint.Zero)
        {
            _log?.Warning("ActorSpawnService: Cannot spawn - no local player");
            return null;
        }
        // No companion slot: the entry IS the actor, not something an owner
        // carries in a slot.
        var actor = SpawnCloneFrom(
            localPlayer,
            reserveCompanionSlot: false,
            modelCharaId: entry.ModelCharaId,
            name: entry.Name,
            kind: entry.Kind);
        return actor;
    }

    public CompanionKind GetSpawnedKind(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return CompanionKind.None;
        try
        {
            var descriptor = _native.ResolveActor(actor.Address);
            return descriptor is { } current
                ? _ownership.GetKind(actor, current)
                : CompanionKind.None;
        }
        catch
        {
            return CompanionKind.None;
        }
    }

    /// <summary>Shared spawn path: new battle character + appearance/position copy.</summary>
    private IActor? SpawnCloneFrom(
        nint sourceAddress,
        bool reserveCompanionSlot,
        int modelCharaId = 0,
        string? name = null,
        CompanionKind kind = CompanionKind.None)
    {
        SpawnOwnershipRecord? ownership = null;
        try
        {
            uint idCheck = _native.CreateBattleCharacter(
                (byte)(reserveCompanionSlot ? 1 : 0));
            if (idCheck == 0xFFFFFFFF)
            {
                _log?.Warning("ActorSpawnService: Failed to create character - invalid ID");
                return null;
            }

            ownership = _ownership.AddPending(
                (ushort)idCheck,
                kind,
                reserveCompanionSlot);

            // Capture ownership before the first native mutation after create.
            // The descriptor is the only safe authority when an object-table
            // index is reused between frames.
            var descriptor = _native.ResolveByIndex((ushort)idCheck);
            if (descriptor is null)
            {
                _log?.Warning("ActorSpawnService: Created character could not be resolved");
                TryDelete(ownership);
                return null;
            }

            ownership.Resolve(descriptor.Value);
            _applySpawnMutations(
                ownership,
                sourceAddress,
                modelCharaId,
                name ?? ToPoserName(descriptor.Value.Index));

            _log?.Debug($"ActorSpawnService: Spawned clone at index {descriptor.Value.Index}");

            // Refresh actor list and find the new actor
            _actorManager.RefreshActors();

            // Refresh can replace the wrapper while the native slot remains
            // occupied. Re-resolve the slot before binding any wrapper.
            var afterRefresh = _native.ResolveByIndex(ownership.Descriptor!.Value.Index);
            if (afterRefresh is null
                || afterRefresh.Value != ownership.Descriptor.Value)
                throw new InvalidOperationException("Spawned actor identity changed after refresh");

            // Find actor by checking the spawned index
            foreach (var actor in _actorManager.Actors)
            {
                if (actor.Address == descriptor.Value.Address)
                {
                    if (!_ownership.Bind(ownership.Token, actor))
                        throw new InvalidOperationException("Spawned actor binding changed");
                    return actor;
                }
            }

            throw new InvalidOperationException("Spawned actor was not present after refresh");
        }
        catch (Exception ex)
        {
            if (ownership is null)
                ownership = _ownership.AddUnresolved(kind, reserveCompanionSlot);
            else
                TryDelete(ownership);
            _log?.Error($"ActorSpawnService: Failed to spawn clone: {ex.Message}");
            return null;
        }
    }

    private void EnsureCurrent(SpawnOwnershipRecord ownership)
    {
        if (ownership.Descriptor is not { } expected)
            throw new InvalidOperationException("Spawned object has no resolved identity");
        var current = _native.ResolveByIndex(expected.Index);
        if (current is null || current.Value != expected)
            throw new InvalidOperationException("Spawned object identity changed");
    }

    private void ApplySpawnMutations(
        SpawnOwnershipRecord ownership,
        nint sourceAddress,
        int modelCharaId,
        string? name)
    {
        if (ownership.Descriptor is not { } descriptor)
            throw new InvalidOperationException("Spawned object has no resolved identity");
        var newObject = (GameObject*)descriptor.Address;
        EnsureCurrent(ownership);
        var newCharacter = (Character*)newObject;

        // Set a name for the character (like Brio does).
        EnsureCurrent(ownership);
        SetName(newObject, name ?? ToPoserName(descriptor.Index));

        // Copy appearance from the source actor.
        var sourceCharacter = (Character*)sourceAddress;
        EnsureCurrent(ownership);
        newCharacter->CharacterSetup.CopyFromCharacter(
            sourceCharacter,
            CharacterSetupContainer.CopyFlags.WeaponHiding | CharacterSetupContainer.CopyFlags.Position);

        // Copy again to trigger redraws for tools like Penumbra.
        EnsureCurrent(ownership);
        newCharacter->CharacterSetup.CopyFromCharacter(
            newCharacter,
            CharacterSetupContainer.CopyFlags.None);

        // Catalog spawns write the model before the first draw.
        if (modelCharaId != 0)
        {
            EnsureCurrent(ownership);
            newCharacter->ModelContainer.ModelCharaId = modelCharaId;
        }

        EnsureCurrent(ownership);
        newObject->Position = sourceCharacter->GameObject.Position;
        newObject->Rotation = sourceCharacter->GameObject.Rotation;
        newObject->DefaultPosition = sourceCharacter->GameObject.Position;
        newObject->DefaultRotation = sourceCharacter->GameObject.Rotation;

        EnsureCurrent(ownership);
        AddCharacterToGPose(newCharacter);

        EnsureCurrent(ownership);
        newObject->EnableDraw();
    }

    private bool TryResolveActorForOperation(
        IActor actor,
        out nint address,
        out SpawnNativeDescriptor? descriptor,
        out SpawnOwnershipRecord? ownership)
    {
        address = actor.Address;
        descriptor = null;
        ownership = null;
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            var current = _native.ResolveActor(actor.Address);
            if (current is null)
            {
                return !_ownership.TryGetBound(actor, out _);
            }
            if (_ownership.TryGetBound(actor, out var bound)
                && !_ownership.TryGetExact(actor, current.Value, out _))
                return false;
            descriptor = current.Value;
            ownership = bound;
            return true;
        }
        catch
        {
            return !_ownership.TryGetBound(actor, out _);
        }
    }

    private bool TryDelete(SpawnOwnershipRecord ownership)
    {
        var result = SpawnOwnershipCleanup.TryDelete(_ownership, _native, ownership);
        if (!result)
            _log?.Warning($"ActorSpawnService: Exact delete pending at index {ownership.CreatedIndex}");
        return result;
    }

    private void AddCharacterToGPose(Character* character)
    {
        if (!_gPoseService.IsGPosing)
            return;

        var ef = EventFramework.Instance();
        if (ef == null)
            return;

        ef->EventSceneModule.EventGPoseController.AddCharacterToGPose(character);
    }

    private static void SetName(GameObject* gameObject, string name)
    {
        for (int x = 0; x < name.Length && x < 64; x++)
        {
            gameObject->Name[x] = (byte)name[x];
        }
        gameObject->Name[Math.Min(name.Length, 63)] = 0;
    }

    private static string ToPoserName(int index)
    {
        // Simple naming: "Poser One", "Poser Two", etc.
        string[] ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
                         "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };

        if (index < 20)
            return $"Poser {ones[index]}";

        return $"Poser {index}";
    }

    public bool DestroyActor(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            if (!_ownership.TryGetBound(actor, out var ownership))
                return false;

            var current = _native.ResolveActor(actor.Address);
            if (current is not null
                && (ownership.Descriptor is not { } expected
                    || expected != current.Value
                    || expected.Address != actor.Address))
            {
                _log?.Warning("ActorSpawnService: Cannot destroy actor - identity mismatch");
                return false;
            }

            if (!TryDelete(ownership))
                return false;

            _log?.Debug($"ActorSpawnService: Destroyed actor at index {ownership.CreatedIndex}");
            _actorManager.RefreshActors();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error($"ActorSpawnService: Failed to destroy actor: {ex.Message}");
            return false;
        }
    }

    public void SetVisibility(IActor actor, bool visible)
    {
        if (actor.Address == nint.Zero)
            return;

        try
        {
            if (!TryResolveActorForOperation(
                    actor,
                    out var address,
                    out var current,
                    out var ownership))
                return;

            var gameObject = (GameObject*)address;
            if (visible)
            {
                gameObject->EnableDraw();
            }
            else
            {
                gameObject->DisableDraw();
            }
            if (ownership is not null && current is { } exact)
                _ownership.TrySetVisibility(actor, exact, visible);
            else if (ownership is not null)
                return;
            if (ownership is not null)
                ownership.SetVisibility(visible);
        }
        catch (Exception ex)
        {
            _log?.Error($"ActorSpawnService: Failed to set visibility: {ex.Message}");
        }

        // The hidden badge lives in the scene snapshot; visibility changes
        // must reconcile it the same way spawn/despawn do.
        _eventBus.Publish(new ActorListChangedEvent(PresentActors()));
    }

    /// <summary>
    /// The event payload every subscriber prunes its state against: auxiliary
    /// bodies (the CharaView preview) are present actors for that purpose and
    /// omitting them would tear their state down on the next visibility toggle.
    /// </summary>
    private IReadOnlyList<IActor> PresentActors()
    {
        var auxiliary = _actorManager.AuxiliaryActors;
        if (auxiliary.Count == 0)
            return _actorManager.Actors;
        var actors = _actorManager.Actors;
        var all = new List<IActor>(actors.Count + auxiliary.Count);
        all.AddRange(actors);
        all.AddRange(auxiliary);
        return all;
    }

    public bool IsVisible(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            if (!TryResolveActorForOperation(
                    actor,
                    out var address,
                    out var current,
                    out var ownership))
                return false;
            if (ownership is not null)
                return ownership.Visible;

            var gameObject = (GameObject*)address;
            return gameObject->IsReadyToDraw();
        }
        catch
        {
            return true;
        }
    }

    public bool SetCompanion(IActor owner, CompanionAttachment container)
    {
        if (!TryResolveActorForOperation(
                owner,
                out var address,
                out var current,
                out var ownership))
            return false;
        var character = (Character*)address;
        if (character == null)
            return false;

        if (character->ChildObject == null)
        {
            _log?.Warning($"ActorSpawnService: actor has no companion slot (spawned without reservation?)");
            return false;
        }

        DestroyCompanion(owner);
        if (container.Kind == CompanionKind.None)
            return true;

        switch (container.Kind)
        {
            case CompanionKind.Companion:
                character->CompanionData.SetupCompanion((short)container.Id, 0);
                break;
            case CompanionKind.Mount:
                character->Mount.CreateAndSetupMount((short)container.Id, 0, 0, 0, 0, 0, 0);
                break;
            case CompanionKind.Ornament:
                character->OrnamentData.SetupOrnament((short)container.Id, 0);
                break;
        }

        // The companion needs a few frames before it can draw. Bounded poll (with a
        // hard timeout + log), not a blind tick delay — matches the redraw policy.
        var want = container;
        PollUntil(
            ownership,
            current,
            () =>
            {
                var chr = (Character*)address;
                if (chr == null || chr->ChildObject == null) return false;
                var info = ReadCompanionInfo(chr);
                var native = &chr->ChildObject->GameObject;
                return info == want && native->IsReadyToDraw();
            },
            () =>
            {
                var chr = (Character*)address;
                if (chr != null && chr->ChildObject != null)
                    chr->ChildObject->GameObject.EnableDraw();
            },
            timeoutMs: 1000,
            what: $"companion {container.Kind} {container.Id}");

        return true;
    }

    public void DestroyCompanion(IActor owner)
    {
        if (!TryResolveActorForOperation(
                owner,
                out var address,
                out _,
                out _))
            return;
        var character = (Character*)address;
        if (character == null || character->ChildObject == null)
            return;

        var info = ReadCompanionInfo(character);
        switch (info.Kind)
        {
            case CompanionKind.Companion:
                character->CompanionData.SetupCompanion(0, 0);
                break;
            case CompanionKind.Mount:
                character->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
                break;
            case CompanionKind.Ornament:
                character->OrnamentData.SetupOrnament(0, 0);
                break;
        }
    }

    public CompanionAttachment GetCompanionInfo(IActor owner)
    {
        if (!TryResolveActorForOperation(
                owner,
                out var address,
                out var current,
                out _))
            return CompanionAttachment.None;
        var character = (Character*)address;
        if (character == null)
            return CompanionAttachment.None;
        return ReadCompanionInfo(character);
    }

    public bool HasCompanionSlot(IActor actor)
    {
        if (!TryResolveActorForOperation(
                actor,
                out var address,
                out _,
                out _))
            return false;
        var character = (Character*)address;
        return character != null && character->ChildObject != null;
    }

    public int GetModelCharaId(IActor actor)
    {
        if (!TryResolveActorForOperation(
                actor,
                out var address,
                out _,
                out _))
            return 0;
        var character = (Character*)address;
        return character == null ? 0 : character->ModelContainer.ModelCharaId;
    }

    public void SetModelCharaId(IActor actor, int modelCharaId)
    {
        if (!TryResolveActorForOperation(
                actor,
                out var address,
                out var current,
                out var ownership))
            return;
        var character = (Character*)address;
        if (character == null
            || character->ModelContainer.ModelCharaId == modelCharaId)
            return;

        // Brio's model change verbatim: write the id, then a full redraw —
        // draw down, wait for ready, draw up. The customize and equipment
        // bytes stay in DrawData behind a creature model, which is what makes
        // writing 0 later bring the human look back.
        character->ModelContainer.ModelCharaId = modelCharaId;
        character->GameObject.DisableDraw();
        PollUntil(
            ownership,
            current,
            () =>
            {
                var gameObject = (GameObject*)address;
                return gameObject != null && gameObject->IsReadyToDraw();
            },
            () =>
            {
                var gameObject = (GameObject*)address;
                if (gameObject != null)
                    gameObject->EnableDraw();
            },
            timeoutMs: 2000,
            what: $"model chara {modelCharaId}");
    }

    private static CompanionAttachment ReadCompanionInfo(Character* native)
    {
        if (native->ChildObject == null)
            return CompanionAttachment.None;

        if (native->OrnamentData.OrnamentObject != null)
            return new(CompanionKind.Ornament, native->OrnamentData.OrnamentId);
        if (native->Mount.MountObject != null)
            return new(CompanionKind.Mount, (ushort)native->Mount.MountId);
        if (native->CompanionData.CompanionObject != null)
            return new(CompanionKind.Companion, (ushort)native->CompanionData.CompanionObject->Character.GameObject.BaseId);

        return CompanionAttachment.None;
    }

    /// <summary>Bounded per-frame poll on the framework thread; logs on timeout.</summary>
    private void PollUntil(
        SpawnOwnershipRecord? ownership,
        SpawnNativeDescriptor? observedLifetime,
        Func<bool> condition,
        Action onSatisfied,
        int timeoutMs,
        string what)
    {
        if (_framework is null)
            return;

        var token = ownership?.Token;
        var lifetime = ownership?.Descriptor ?? observedLifetime;
        var deadline = System.Environment.TickCount64 + timeoutMs;
        void Tick(IFramework fw)
        {
            try
            {
                if (!IsCallbackCurrent(token, lifetime))
                {
                    _framework.Update -= Tick;
                    return;
                }
                if (condition())
                {
                    if (!IsCallbackCurrent(token, lifetime))
                    {
                        _framework.Update -= Tick;
                        return;
                    }
                    onSatisfied();
                    _framework.Update -= Tick;
                }
                else if (System.Environment.TickCount64 > deadline)
                {
                    _log?.Warning($"ActorSpawnService: timed out waiting for {what}");
                    _framework.Update -= Tick;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"ActorSpawnService: poll for {what} failed: {ex.Message}");
                _framework.Update -= Tick;
            }
        }
        _framework.Update += Tick;
    }

    private bool IsCallbackCurrent(
        Guid? token,
        SpawnNativeDescriptor? lifetime)
    {
        if (_disposed)
            return false;
        if (lifetime is null)
            return true;
        if (_native.ResolveByIndex(lifetime.Value.Index) != lifetime.Value)
            return false;
        return token is null
            || _ownership.TryGetExact(token.Value, lifetime.Value, out _);
    }

    internal bool InvokeOwnedCallbackForTests(
        Guid token,
        SpawnNativeDescriptor lifetime,
        Action callback)
    {
        if (!IsCallbackCurrent(token, lifetime))
            return false;
        callback();
        return true;
    }

    public bool IsSpawnedActor(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            var current = _native.ResolveActor(actor.Address);
            return current is { } descriptor
                && _ownership.TryGetExact(actor, descriptor, out _);
        }
        catch
        {
            return false;
        }
    }

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

        var deleted = false;
        foreach (var ownership in _ownership.Snapshot)
        {
            if (TryDelete(ownership))
                deleted = true;
            else
                _log?.Warning($"ActorSpawnService: Retaining pending actor at index {ownership.CreatedIndex}");
        }

        if (deleted)
            _actorManager.RefreshActors();
    }

    public void Dispose()
    {
        _disposed = true;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        DestroyAllSpawned();
    }
}
