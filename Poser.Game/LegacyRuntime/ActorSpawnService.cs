using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Exact native identity of one client-object slot occupant.
/// <para>
/// <see cref="Index"/> is the <c>ClientObjectManager</c> slot index — the
/// number <c>CreateBattleCharacter</c> returns and the only number
/// <c>GetObjectByIndex</c>/<c>GetIndexByObject</c>/<c>DeleteObjectByIndex</c>
/// accept. It is NOT <c>GameObject.ObjectIndex</c>: the global object-table
/// index of a client object is its slot plus 200 (Brio converts explicitly,
/// <c>EntityActorManager.cs:74</c>, <c>go.ObjectIndex - 200</c>). Mixing the
/// two spaces means deleting a foreign object, so every index that reaches
/// this type comes from the ClientObjectManager API and nowhere else.
/// </para>
/// <see cref="LifetimeStamp"/> is the destruction-sequence stamp for
/// <see cref="Address"/> at resolve time. It advances inside the native
/// Character finalize hook — the game's own lifetime transition — never at
/// adapter observation, so an external delete-and-reuse with an identical
/// index/address/EntityId still compares unequal and fails closed.
/// </summary>
internal readonly record struct SpawnNativeDescriptor(
    ushort Index,
    nint Address,
    ulong EntityId,
    ulong LifetimeStamp = 0);

internal enum SpawnOwnershipState
{
    PendingCreate,
    Live,
    PendingDelete,

    /// <summary>
    /// Create faulted without yielding a usable identity. The record is
    /// retained as an explicit readout (snapshot + one Error log) and is
    /// never allowed to touch native state.
    /// </summary>
    NonRecoverable,
}

/// <summary>
/// Destruction bookkeeping fed by the native Character finalize hook. This is
/// the production transition logic shared by the real adapter and driven
/// directly by tests; the hook only forwards (address, index) into it.
/// </summary>
internal sealed class SpawnLifetimeStamps
{
    // Character objects are pool-allocated, so distinct destroyed addresses
    // stay small in practice; the cap only bounds a pathological session.
    // On overflow the maps clear while the sequence keeps rising, and the
    // clear raises the unknown-entry floor to the current sequence: every
    // pre-clear stamp — including the implicit stamp 0 of a never-destroyed
    // entry — is strictly below the floor, so a post-clear resolve can only
    // mismatch a stored descriptor (refusal/retire of our own records),
    // never compare equal and claim a foreign object.
    private const int MaxTrackedAddresses = 8192;

    private readonly object _gate = new();
    private readonly Dictionary<nint, ulong> _byAddress = new();
    private readonly Dictionary<ushort, ulong> _byIndex = new();
    private ulong _sequence;
    private ulong _clearFloor;

    public void NoteDestroyed(nint address, ushort? index)
    {
        lock (_gate)
        {
            _sequence++;
            if (_byAddress.Count >= MaxTrackedAddresses
                && !_byAddress.ContainsKey(address))
            {
                _byAddress.Clear();
                _byIndex.Clear();
                _clearFloor = _sequence;
            }
            _byAddress[address] = _sequence;
            if (index is { } known)
                _byIndex[known] = _sequence;
        }
    }

    public ulong StampFor(nint address)
    {
        lock (_gate)
        {
            return _byAddress.GetValueOrDefault(address, _clearFloor);
        }
    }

    public ulong IndexStampFor(ushort index)
    {
        lock (_gate)
        {
            return _byIndex.GetValueOrDefault(index, _clearFloor);
        }
    }
}

internal sealed class SpawnOwnershipRecord
{
    public SpawnOwnershipRecord(
        Guid token,
        ushort createdIndex,
        SpawnNativeDescriptor? descriptor,
        CompanionKind? kind,
        bool hasCompanionSlot,
        ulong createIndexStamp = 0)
    {
        Token = token;
        CreatedIndex = createdIndex;
        Descriptor = descriptor;
        Kind = kind;
        HasCompanionSlot = hasCompanionSlot;
        CreateIndexStamp = createIndexStamp;
    }

    public Guid Token { get; }
    public ushort CreatedIndex { get; }
    public SpawnNativeDescriptor? Descriptor { get; private set; }
    public IActor? Actor { get; private set; }

    /// <summary>Wrapper logical identity captured at bind; later exact
    /// lookups require both the same instance and the same id.</summary>
    public EntityId? BoundId { get; private set; }
    /// <summary>The catalog kind this record was spawned as, or null for a
    /// plain spawn or clone — those are actors, not catalog entries.</summary>
    public CompanionKind? Kind { get; }
    public bool HasCompanionSlot { get; }

    /// <summary>Per-index destruction stamp at create time. Unchanged means
    /// no object at the created index has been destroyed since our create,
    /// i.e. the current occupant is the object our create call made.</summary>
    public ulong CreateIndexStamp { get; }
    public bool Visible { get; private set; } = true;
    public SpawnOwnershipState State { get; private set; } = SpawnOwnershipState.PendingCreate;

    /// <summary>Adopts an identity resolved for <see cref="CreatedIndex"/>.
    /// The slot check is an invariant assertion, not a policy: the adapter
    /// resolves BY that slot, so a differing slot means the descriptor was
    /// built in the wrong index space and nothing about it can be trusted.
    /// Returns false instead of throwing so the per-frame recovery tick can
    /// treat it as "outcome still unknown" without faulting every frame.</summary>
    public bool TryResolve(SpawnNativeDescriptor descriptor)
    {
        if (descriptor.Index != CreatedIndex)
            return false;
        Descriptor = descriptor;
        State = SpawnOwnershipState.Live;
        return true;
    }

    public void Resolve(SpawnNativeDescriptor descriptor)
    {
        if (!TryResolve(descriptor))
            throw new InvalidOperationException("Spawned object index changed");
    }

    public void Bind(IActor actor)
    {
        Actor = actor;
        BoundId = actor.Id;
    }

    public void MarkPending() => State = SpawnOwnershipState.PendingDelete;
    public void MarkNonRecoverable() => State = SpawnOwnershipState.NonRecoverable;
    public void SetVisibility(bool visible) => Visible = visible;
}

internal sealed class SpawnOwnershipLedger
{
    private readonly Dictionary<Guid, SpawnOwnershipRecord> _records = new();

    public IReadOnlyList<SpawnOwnershipRecord> Snapshot => _records.Values.ToArray();

    public SpawnOwnershipRecord Add(
        SpawnNativeDescriptor descriptor,
        CompanionKind? kind,
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
        CompanionKind? kind,
        bool hasCompanionSlot,
        ulong createIndexStamp)
    {
        var record = new SpawnOwnershipRecord(
            Guid.NewGuid(),
            index,
            null,
            kind,
            hasCompanionSlot,
            createIndexStamp);
        _records.Add(record.Token, record);
        return record;
    }

    public SpawnOwnershipRecord AddNonRecoverable(
        CompanionKind? kind,
        bool hasCompanionSlot)
    {
        var record = new SpawnOwnershipRecord(
            Guid.NewGuid(),
            ushort.MaxValue,
            null,
            kind,
            hasCompanionSlot);
        record.MarkNonRecoverable();
        _records.Add(record.Token, record);
        return record;
    }

    public bool Bind(Guid token, IActor actor, EntityId expectedId)
    {
        return _records.TryGetValue(token, out var record)
            && record.State == SpawnOwnershipState.Live
            && record.Descriptor is { Address: var address }
            && address == actor.Address
            && actor.Id == expectedId
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
            candidate.Actor is not null
            && ReferenceEquals(candidate.Actor, actor)
            && candidate.BoundId == actor.Id)!;
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
            && (candidate.Actor is null
                || (ReferenceEquals(candidate.Actor, actor)
                    && candidate.BoundId == actor.Id)))!;
        return record is not null;
    }

    public CompanionKind? GetKind(IActor actor, SpawnNativeDescriptor descriptor) =>
        TryGetExact(actor, descriptor, out var record)
            ? record.Kind
            : null;

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

    /// <summary>Retires a create that never gained an identity: only legal
    /// while the record is still PendingCreate (nothing native to clean).</summary>
    public bool RetirePendingCreate(Guid token)
    {
        if (!_records.TryGetValue(token, out var record)
            || record.State != SpawnOwnershipState.PendingCreate)
            return false;
        _records.Remove(token);
        return true;
    }

    /// <summary>Drops a readout the caller has proven is about nothing: only
    /// legal for a NonRecoverable record, and the caller owes the proof that
    /// its created slot is vacated (the record never had a descriptor, so
    /// there is nothing native to clean either way).</summary>
    public bool RetireNonRecoverable(Guid token)
    {
        if (!_records.TryGetValue(token, out var record)
            || record.State != SpawnOwnershipState.NonRecoverable)
            return false;
        _records.Remove(token);
        return true;
    }

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

/// <summary>
/// The sole native boundary for spawn ownership. Every dereference primitive
/// revalidates the exact descriptor immediately before touching memory and
/// refuses on any mismatch — unresolved identity is never permission.
/// </summary>
internal interface IActorSpawnNativeAdapter
{
    bool IsAvailable { get; }

    /// <summary>True while the Character finalize hook is installed. Without
    /// it no authority may span frames: spawning and delayed callbacks
    /// refuse (fail-closed narrowing).</summary>
    bool IsLifetimeAuthoritative { get; }
    string? LifetimeAuthorityDetail { get; }

    uint CreateBattleCharacter(byte reserveCompanionSlot);
    ulong IndexDestructionStamp(ushort index);
    SpawnNativeDescriptor? ResolveByIndex(ushort index);
    SpawnNativeDescriptor? ResolveActor(nint address);
    bool DeleteExact(SpawnNativeDescriptor descriptor);

    bool SetDrawState(SpawnNativeDescriptor descriptor, bool visible);
    bool? IsReadyToDraw(SpawnNativeDescriptor descriptor);
    bool HasCompanionSlot(SpawnNativeDescriptor descriptor);
    /// <summary>Reads the slot. False when the descriptor no longer
    /// revalidates — an unreadable actor is NOT an empty slot, and only the
    /// empty slot may be written over. On true, a null
    /// <paramref name="attachment"/> is the empty slot.</summary>
    bool TryReadCompanion(
        SpawnNativeDescriptor descriptor,
        out CompanionAttachment? attachment);

    bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id);
    bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want);
    bool EnableCompanionDraw(SpawnNativeDescriptor descriptor);
    int? ReadModelCharaId(SpawnNativeDescriptor descriptor);
    bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId);
}

internal unsafe sealed class ActorSpawnNativeAdapter : IActorSpawnNativeAdapter, IDisposable
{
    // Brio ObjectMonitorService.cs: the native Character destructor. Hooking
    // it is what makes destruction stamps authoritative — Brio consumes the
    // same transition (OnCharacterDestroyed) to prune created indexes after
    // external deletion, and resolves the dying object's COM index inside
    // the callback exactly as we do.
    private const string CharacterFinalizeSig =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? 48 81 C1";

    private delegate nint CharacterFinalizeDelegate(Character* chara);

    private readonly SpawnLifetimeStamps _stamps = new();
    private readonly Hook<CharacterFinalizeDelegate>? _finalizeHook;
    private readonly string? _lifetimeAuthorityDetail;

    public ActorSpawnNativeAdapter(
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog? log)
    {
        try
        {
            var address = sigScanner.ScanText(CharacterFinalizeSig);
            _finalizeHook = hooking.HookFromAddress<CharacterFinalizeDelegate>(
                address, CharacterFinalizeDetour);
            _finalizeHook.Enable();
        }
        catch (Exception ex)
        {
            _finalizeHook?.Dispose();
            _finalizeHook = null;
            _lifetimeAuthorityDetail =
                $"Character finalize hook unavailable: {ex.Message}";
            log?.Warning(
                $"ActorSpawnService: {_lifetimeAuthorityDetail} - spawning disabled");
        }
    }

    public bool IsAvailable => ClientObjectManager.Instance() is not null;
    public bool IsLifetimeAuthoritative => _finalizeHook is not null;
    public string? LifetimeAuthorityDetail => _lifetimeAuthorityDetail;

    private nint CharacterFinalizeDetour(Character* chara)
    {
        try
        {
            ushort? index = null;
            var com = ClientObjectManager.Instance();
            if (com is not null)
            {
                var i = com->GetIndexByObject((GameObject*)chara);
                if (i != 0xFFFFFFFF)
                    index = (ushort)i;
            }
            _stamps.NoteDestroyed((nint)chara, index);
        }
        catch
        {
            // Never fault the native destructor.
        }
        return _finalizeHook!.Original(chara);
    }

    public uint CreateBattleCharacter(byte reserveCompanionSlot)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return 0xFFFFFFFF;
        // param:, never positional. The signature is
        // CreateBattleCharacter(uint index = uint.MaxValue, byte param = 0):
        // the FIRST parameter names the slot to create in, and a byte binds to
        // it silently. Passing the companion flag positionally asked the game
        // to build the clone in client-object slot 0/1 — object-table 200/201,
        // the GPose primary — and never reserved the slot. Brio names it for
        // the same reason (ActorSpawnService.cs:309).
        return com->CreateBattleCharacter(param: reserveCompanionSlot);
    }

    public ulong IndexDestructionStamp(ushort index) => _stamps.IndexStampFor(index);

    public SpawnNativeDescriptor? ResolveByIndex(ushort index)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return null;
        var native = com->GetObjectByIndex(index);
        if (native is null)
            return null;
        // The slot we asked for, not native->ObjectIndex: GetObjectByIndex
        // reads ClientObjectManager's own array, so the occupant's slot IS
        // `index`, while its ObjectIndex is the global object-table number
        // (slot + 200). Carrying the global number here fed it straight back
        // into GetObjectByIndex/DeleteObjectByIndex.
        return new SpawnNativeDescriptor(
            index,
            (nint)native,
            native->EntityId,
            _stamps.StampFor((nint)native));
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

    /// <summary>Revalidates the exact descriptor and returns the live native
    /// object, or null when identity cannot be proven right now.</summary>
    private GameObject* Revalidate(SpawnNativeDescriptor descriptor)
    {
        var current = ResolveByIndex(descriptor.Index);
        return current is { } resolved && resolved == descriptor
            ? (GameObject*)descriptor.Address
            : null;
    }

    public bool SetDrawState(SpawnNativeDescriptor descriptor, bool visible)
    {
        var gameObject = Revalidate(descriptor);
        if (gameObject is null)
            return false;
        if (visible)
            gameObject->EnableDraw();
        else
            gameObject->DisableDraw();
        return true;
    }

    public bool? IsReadyToDraw(SpawnNativeDescriptor descriptor)
    {
        var gameObject = Revalidate(descriptor);
        if (gameObject is null)
            return null;
        return gameObject->IsReadyToDraw();
    }

    public bool HasCompanionSlot(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        return character != null && character->ChildObject != null;
    }

    public bool TryReadCompanion(
        SpawnNativeDescriptor descriptor,
        out CompanionAttachment? attachment)
    {
        attachment = null;
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        attachment = ReadCompanionInfo(character);
        return true;
    }

    public bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        switch (kind)
        {
            case CompanionKind.Companion:
                character->CompanionData.SetupCompanion(id, 0);
                break;
            case CompanionKind.Mount:
                character->Mount.CreateAndSetupMount(id, 0, 0, 0, 0, 0, 0);
                break;
            case CompanionKind.Ornament:
                character->OrnamentData.SetupOrnament(id, 0);
                break;
            default:
                return false;
        }
        return true;
    }

    public bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null || character->ChildObject == null)
            return false;
        var info = ReadCompanionInfo(character);
        var native = &character->ChildObject->GameObject;
        return info == want && native->IsReadyToDraw();
    }

    public bool EnableCompanionDraw(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null || character->ChildObject == null)
            return false;
        character->ChildObject->GameObject.EnableDraw();
        return true;
    }

    public int? ReadModelCharaId(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return null;
        return character->ModelContainer.ModelCharaId;
    }

    public bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        // Brio's model change verbatim: write the id, then a full redraw —
        // draw down, wait for ready, draw up. The customize and equipment
        // bytes stay in DrawData behind a creature model, which is what makes
        // writing 0 later bring the human look back.
        character->ModelContainer.ModelCharaId = modelCharaId;
        character->GameObject.DisableDraw();
        return true;
    }

    private static CompanionAttachment? ReadCompanionInfo(Character* native)
    {
        if (native->ChildObject == null)
            return null;

        if (native->OrnamentData.OrnamentObject != null)
            return new CompanionAttachment(
                CompanionKind.Ornament, native->OrnamentData.OrnamentId);
        if (native->Mount.MountObject != null)
            return new CompanionAttachment(
                CompanionKind.Mount, (ushort)native->Mount.MountId);
        if (native->CompanionData.CompanionObject != null)
            return new CompanionAttachment(
                CompanionKind.Companion,
                (ushort)native->CompanionData.CompanionObject->Character.GameObject.BaseId);

        return null;
    }

    public void Dispose() => _finalizeHook?.Dispose();
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
            if (ownership.State == SpawnOwnershipState.NonRecoverable)
                return false;
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
    private const int CreateRecoveryTimeoutMs = 5000;

    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog? _log;
    private readonly IFramework? _framework;
    private readonly Func<nint> _localPlayerAddress;
    private readonly Func<nint, EntityId?> _expectedWrapperIdentity;
    private readonly Func<long> _clock;
    private readonly bool _ownsAdapter;

    private readonly IActorSpawnNativeAdapter _native;
    private readonly Action<SpawnOwnershipRecord, nint, int, string?> _applySpawnMutations;
    private readonly SpawnOwnershipLedger _ownership = new();

    // Legacy-compatible visibility overrides for actors Poser did not spawn,
    // keyed by the EXACT descriptor (address+EntityId+lifetime stamp) so an
    // override dies with the native lifetime and can never transfer across
    // slot reuse. Cleared with the GPose session.
    private readonly Dictionary<SpawnNativeDescriptor, bool> _legacyVisibility = new();
    private const int MaxLegacyVisibilityEntries = 256;

    private bool _spawnUnavailableLogged;
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
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking)
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
            ownsAdapter: true)
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
        bool ownsAdapter = false)
    {
        _framework = framework;
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _log = log;
        _native = native;
        _localPlayerAddress = localPlayerAddress;
        _applySpawnMutations = applySpawnMutations ?? ApplySpawnMutations;
        // Fail closed: without a way to derive the expected wrapper identity,
        // no bind can be proven and spawn rolls back.
        _expectedWrapperIdentity = expectedWrapperIdentity ?? (_ => null);
        _clock = clock ?? (() => System.Environment.TickCount64);
        _ownsAdapter = ownsAdapter;

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
            : new EntityId($"actor_{gameObject.GameObjectId}");
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

    public IActor? SpawnNewActor(bool reserveCompanionSlot)
    {
        if (!OnOwnerThread || !SpawnAuthorityAvailable())
            return null;
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
        return SpawnCloneFrom(resolvedSource.Value.Address, reserveCompanionSlot: true);
    }

    /// <summary>
    /// Clones an OVERWORLD source into a Poser-owned GPose actor through the
    /// same owned spawn transaction as every other clone. Overworld objects
    /// are not ClientObjectManager-resolvable (both references use
    /// GetIndexByObject only on client objects they created — Brio
    /// ActorSpawnService.cs:205, Ktisis ActorModule.cs:140-144), so the COM
    /// gate <see cref="CloneActor"/> uses cannot prove them; the caller —
    /// <see cref="WorldActorDiscovery"/>, the only caller — has just proven
    /// the source's exact (reference, address, index, GameObjectId) identity
    /// through its own object-table observation on this same framework tick.
    /// The source is only READ (appearance/position seed copy); the ownership
    /// record covers the clone alone, so no authority over the source is
    /// ever taken.
    /// </summary>
    internal IActor? CloneFromWorldSource(nint sourceAddress)
    {
        if (!OnOwnerThread || !SpawnAuthorityAvailable())
            return null;
        if (sourceAddress == nint.Zero)
        {
            _log?.Warning(
                "ActorSpawnService: Cannot clone world source - no address");
            return null;
        }
        // A world clone keeps the companion slot, exactly like CloneActor.
        return SpawnCloneFrom(sourceAddress, reserveCompanionSlot: true);
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
        var actor = SpawnCloneFrom(
            localPlayer,
            reserveCompanionSlot: false,
            modelCharaId: entry.ModelCharaId,
            name: entry.Name,
            kind: entry.Kind);
        return actor;
    }

    public CompanionKind? GetSpawnedKind(IActor actor)
    {
        if (actor.Address == nint.Zero || !OnOwnerThread)
            return null;
        try
        {
            var descriptor = _native.ResolveActor(actor.Address);
            return descriptor is { } current
                ? _ownership.GetKind(actor, current)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Shared spawn path: new battle character + appearance/position copy.</summary>
    private IActor? SpawnCloneFrom(
        nint sourceAddress,
        bool reserveCompanionSlot,
        int modelCharaId = 0,
        string? name = null,
        CompanionKind? kind = null)
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

            // The per-index destruction stamp taken here is the create-time
            // baseline: while it is unchanged, no object at this index has
            // been destroyed, so the occupant is the object we just created.
            ownership = _ownership.AddPending(
                (ushort)idCheck,
                kind,
                reserveCompanionSlot,
                _native.IndexDestructionStamp((ushort)idCheck));

            // Capture ownership before the first native mutation after create.
            // The descriptor is the only safe authority when an object-table
            // index is reused between frames.
            var descriptor = _native.ResolveByIndex((ushort)idCheck);
            if (descriptor is null)
            {
                _log?.Warning("ActorSpawnService: Created character could not be resolved");
                ScheduleCreateRecovery(ownership);
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

            // Bind requires the wrapper's logical identity, not just its
            // address: a wrapper minted for a different logical entity at the
            // same address must refuse.
            var expectedId = _expectedWrapperIdentity(descriptor.Value.Address);
            if (expectedId is null)
                throw new InvalidOperationException("Spawned wrapper identity could not be derived");

            foreach (var actor in _actorManager.Actors)
            {
                if (actor.Address == descriptor.Value.Address)
                {
                    if (actor.Id != expectedId.Value)
                        throw new InvalidOperationException("Spawned wrapper identity mismatch after refresh");
                    if (!_ownership.Bind(ownership.Token, actor, expectedId.Value))
                        throw new InvalidOperationException("Spawned actor binding changed");
                    return actor;
                }
            }

            throw new InvalidOperationException("Spawned actor was not present after refresh");
        }
        catch (Exception ex)
        {
            if (ownership is null)
            {
                // Create itself faulted: no index is known, so there is
                // nothing native we could ever prove ownership of again.
                _ownership.AddNonRecoverable(kind, reserveCompanionSlot);
                _log?.Error(
                    $"ActorSpawnService: create faulted without an index; retained as non-recoverable readout: {ex.Message}");
            }
            else if (ownership.State == SpawnOwnershipState.PendingCreate)
            {
                ScheduleCreateRecovery(ownership);
                _log?.Error($"ActorSpawnService: Failed to spawn clone: {ex.Message}");
            }
            else
            {
                var deleted = TryDelete(ownership);
                _log?.Error($"ActorSpawnService: Failed to spawn clone: {ex.Message}");
                if (deleted)
                {
                    // The transaction refreshed the actor list mid-flight, so a
                    // wrapper for the object we just deleted is still published.
                    // DestroyActor refreshes after its delete for the same
                    // reason; the rollback arm owes the list the same repair
                    // rather than leaving a dead wrapper until the next scan.
                    // Guarded because this runs from a catch arm: the refresh
                    // publishes, and a faulting subscriber must not replace the
                    // spawn failure with a second, unrelated exception.
                    try
                    {
                        _actorManager.RefreshActors();
                    }
                    catch (Exception refreshEx)
                    {
                        _log?.Error(
                            $"ActorSpawnService: rollback refresh failed; a deleted wrapper may persist until the next scan: {refreshEx.Message}");
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Finishes a create whose first resolve failed. Terminal outcomes:
    /// promotion to exact PendingDelete plus a delete attempt once the
    /// per-index destruction stamp proves the occupant is ours, or retirement
    /// once that stamp shows our created object was destroyed. Returns false
    /// while the outcome is still unknown (caller retries).
    /// </summary>
    private bool TryFinishPendingCreate(SpawnOwnershipRecord ownership)
    {
        if (ownership.State != SpawnOwnershipState.PendingCreate
            || ownership.CreatedIndex == ushort.MaxValue)
            return false;

        if (_native.IndexDestructionStamp(ownership.CreatedIndex) != ownership.CreateIndexStamp)
        {
            // The finalize hook observed a destruction at our created index
            // since create: our object is gone and any occupant is foreign.
            _ownership.RetirePendingCreate(ownership.Token);
            _log?.Debug(
                $"ActorSpawnService: created index {ownership.CreatedIndex} was destroyed externally; record retired");
            return true;
        }

        SpawnNativeDescriptor? current;
        try
        {
            current = _native.ResolveByIndex(ownership.CreatedIndex);
        }
        catch
        {
            // A resolution fault leaves the outcome unknown: retain the
            // record for the next retry; bulk cleanup must not throw.
            return false;
        }
        if (current is null)
            return false;

        // Unchanged destruction stamp: the occupant is the object our create
        // call made, so its identity is now authoritative. The spawn already
        // failed, so the record promotes straight to exact pending deletion.
        if (!ownership.TryResolve(current.Value))
            return false;
        ownership.MarkPending();
        TryDelete(ownership);
        return true;
    }

    private void ScheduleCreateRecovery(SpawnOwnershipRecord ownership)
    {
        if (ownership.CreatedIndex == ushort.MaxValue)
            return;
        if (_framework is null)
            return; // GPose-exit/dispose cleanup still promotes synchronously.

        var deadline = _clock() + CreateRecoveryTimeoutMs;
        // The tick runs every frame until a terminal outcome; a fault that
        // reproduces every frame must be said once, not once per frame.
        var faultLogged = false;
        void Tick(IFramework fw)
        {
            try
            {
                if (_disposed || ownership.State != SpawnOwnershipState.PendingCreate)
                {
                    _framework.Update -= Tick;
                    return;
                }
                if (TryFinishPendingCreate(ownership))
                {
                    _framework.Update -= Tick;
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!faultLogged)
                {
                    faultLogged = true;
                    _log?.Warning(
                        $"ActorSpawnService: pending-create recovery for index {ownership.CreatedIndex} faulted: {ex.Message}");
                }
            }
            if (_clock() > deadline)
            {
                ownership.MarkNonRecoverable();
                _log?.Error(
                    $"ActorSpawnService: created index {ownership.CreatedIndex} could not be recovered; retained as non-recoverable readout");
                _framework.Update -= Tick;
            }
        }
        _framework.Update += Tick;
    }

    /// <summary>
    /// The single fail-closed resolution gate for operations on arbitrary
    /// actors. Unresolved identity (null or fault) refuses the operation —
    /// it is never permission to dereference a raw address. A wrapper bound
    /// to an ownership record additionally requires its exact descriptor.
    /// </summary>
    private bool TryResolveActorForOperation(
        IActor actor,
        out SpawnNativeDescriptor descriptor,
        out SpawnOwnershipRecord? ownership)
    {
        descriptor = default;
        ownership = null;
        if (actor.Address == nint.Zero)
            return false;

        SpawnNativeDescriptor? current;
        try
        {
            current = _native.ResolveActor(actor.Address);
        }
        catch
        {
            return false;
        }
        if (current is null)
            return false;

        if (_ownership.TryGetBound(actor, out var bound))
        {
            if (!_ownership.TryGetExact(actor, current.Value, out _))
                return false;
            ownership = bound;
        }

        descriptor = current.Value;
        return true;
    }

    private bool TryDelete(SpawnOwnershipRecord ownership)
    {
        var result = SpawnOwnershipCleanup.TryDelete(_ownership, _native, ownership);
        if (!result)
            _log?.Warning($"ActorSpawnService: Exact delete pending at index {ownership.CreatedIndex}");
        return result;
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

    private void EnsureCurrent(SpawnOwnershipRecord ownership)
    {
        if (ownership.Descriptor is not { } expected)
            throw new InvalidOperationException("Spawned object has no resolved identity");
        var current = _native.ResolveByIndex(expected.Index);
        if (current is null || current.Value != expected)
            throw new InvalidOperationException("Spawned object identity changed");
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
        if (actor.Address == nint.Zero || !OnOwnerThread)
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
        if (actor.Address == nint.Zero || !OnOwnerThread)
            return;

        try
        {
            if (!TryResolveActorForOperation(actor, out var descriptor, out var ownership))
                return;
            if (!_native.SetDrawState(descriptor, visible))
                return;

            if (ownership is not null)
                _ownership.TrySetVisibility(actor, descriptor, visible);
            else
                RememberLegacyVisibility(descriptor, visible);
        }
        catch (Exception ex)
        {
            _log?.Error($"ActorSpawnService: Failed to set visibility: {ex.Message}");
            return;
        }

        // The hidden badge lives in the scene snapshot; visibility changes
        // must reconcile it the same way spawn/despawn do.
        _eventBus.Publish(new ActorListChangedEvent(PresentActors()));
    }

    private void RememberLegacyVisibility(SpawnNativeDescriptor descriptor, bool visible)
    {
        // Bounded fail-safe: dropping overrides only loses badge state, and
        // only in a pathological session.
        if (_legacyVisibility.Count >= MaxLegacyVisibilityEntries
            && !_legacyVisibility.ContainsKey(descriptor))
            _legacyVisibility.Clear();
        _legacyVisibility[descriptor] = visible;
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
        if (actor.Address == nint.Zero || !OnOwnerThread)
            return false;

        try
        {
            if (!TryResolveActorForOperation(actor, out var descriptor, out var ownership))
                return false;
            if (ownership is not null)
                return ownership.Visible;
            if (_legacyVisibility.TryGetValue(descriptor, out var overrideValue))
                return overrideValue;

            return _native.IsReadyToDraw(descriptor) ?? false;
        }
        catch
        {
            return false;
        }
    }

    public bool SetCompanion(IActor owner, CompanionAttachment? container)
    {
        if (!OnOwnerThread)
            return false;
        if (!TryResolveActorForOperation(owner, out var descriptor, out var ownership))
            return false;

        if (!_native.HasCompanionSlot(descriptor))
        {
            _log?.Warning($"ActorSpawnService: actor has no companion slot (spawned without reservation?)");
            return false;
        }

        // An unreadable slot is not an empty one: only a slot we could read
        // may be emptied and refilled.
        if (!_native.TryReadCompanion(descriptor, out var existing))
            return false;
        if (existing is { } attached
            && !_native.WriteCompanion(descriptor, attached.Kind, 0))
            return false;
        if (container is not { } want)
            return true;

        if (!_native.WriteCompanion(descriptor, want.Kind, (short)want.Id))
            return false;

        // The companion needs a few frames before it can draw. Bounded poll (with a
        // hard timeout + log), not a blind tick delay — matches the redraw policy.
        PollUntil(
            ownership,
            descriptor,
            () => _native.IsCompanionReady(descriptor, want),
            () => _native.EnableCompanionDraw(descriptor),
            timeoutMs: 1000,
            what: $"companion {want.Kind} {want.Id}");

        return true;
    }

    public void DestroyCompanion(IActor owner)
    {
        if (!OnOwnerThread)
            return;
        if (!TryResolveActorForOperation(owner, out var descriptor, out _))
            return;

        if (!_native.TryReadCompanion(descriptor, out var info)
            || info is not { } attached)
            return;
        _native.WriteCompanion(descriptor, attached.Kind, 0);
    }

    public CompanionAttachment? GetCompanionInfo(IActor owner)
    {
        if (!OnOwnerThread)
            return null;
        if (!TryResolveActorForOperation(owner, out var descriptor, out _))
            return null;
        return _native.TryReadCompanion(descriptor, out var info) ? info : null;
    }

    public bool HasCompanionSlot(IActor actor)
    {
        if (!OnOwnerThread)
            return false;
        if (!TryResolveActorForOperation(actor, out var descriptor, out _))
            return false;
        return _native.HasCompanionSlot(descriptor);
    }

    public int GetModelCharaId(IActor actor)
    {
        if (!OnOwnerThread)
            return 0;
        if (!TryResolveActorForOperation(actor, out var descriptor, out _))
            return 0;
        return _native.ReadModelCharaId(descriptor) ?? 0;
    }

    public void SetModelCharaId(IActor actor, int modelCharaId)
    {
        if (!OnOwnerThread)
            return;
        if (!TryResolveActorForOperation(actor, out var descriptor, out var ownership))
            return;
        if (_native.ReadModelCharaId(descriptor) is not { } currentId
            || currentId == modelCharaId)
            return;

        if (!_native.WriteModelCharaIdAndBeginRedraw(descriptor, modelCharaId))
            return;
        PollUntil(
            ownership,
            descriptor,
            () => _native.IsReadyToDraw(descriptor) == true,
            () => _native.SetDrawState(descriptor, true),
            timeoutMs: 2000,
            what: $"model chara {modelCharaId}");
    }

    /// <summary>
    /// Bounded per-frame poll on the framework thread; logs on timeout. Never
    /// runs without an exact descriptor, and refuses outright when the
    /// lifetime hook is absent: a delayed callback cannot prove its target is
    /// still the same object across frames without the authoritative
    /// destruction transition.
    /// </summary>
    private void PollUntil(
        SpawnOwnershipRecord? ownership,
        SpawnNativeDescriptor lifetime,
        Func<bool> condition,
        Action onSatisfied,
        int timeoutMs,
        string what)
    {
        if (_framework is null)
            return;
        if (!_native.IsLifetimeAuthoritative)
        {
            _log?.Warning(
                $"ActorSpawnService: delayed {what} skipped - no authoritative lifetime");
            return;
        }

        var token = ownership?.Token;
        var deadline = _clock() + timeoutMs;
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
                else if (_clock() > deadline)
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
        SpawnNativeDescriptor lifetime)
    {
        if (_disposed)
            return false;
        if (_native.ResolveByIndex(lifetime.Index) != lifetime)
            return false;
        return token is null
            || _ownership.TryGetExact(token.Value, lifetime, out _);
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
        if (actor.Address == nint.Zero || !OnOwnerThread)
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

    /// <summary>
    /// Proof that a readout record is about nothing that still exists: the
    /// finalize hook recorded a destruction at the created slot since create,
    /// or the slot resolves empty while the manager is available. Absence of
    /// proof — no index was ever known, the manager is unavailable, the
    /// resolve faults, or the slot is still occupied — is never vacancy.
    /// </summary>
    private bool IsCreatedIndexProvablyVacated(SpawnOwnershipRecord ownership)
    {
        if (ownership.CreatedIndex == ushort.MaxValue)
            return false;
        try
        {
            if (!_native.IsAvailable)
                return false;
            if (_native.IndexDestructionStamp(ownership.CreatedIndex)
                != ownership.CreateIndexStamp)
                return true;
            return _native.ResolveByIndex(ownership.CreatedIndex) is null;
        }
        catch
        {
            return false;
        }
    }

    private void DestroyAllSpawned()
    {
        _log?.Debug("ActorSpawnService: Destroying all spawned actors");

        var deleted = false;
        foreach (var ownership in _ownership.Snapshot)
        {
            if (ownership.State == SpawnOwnershipState.NonRecoverable)
            {
                if (IsCreatedIndexProvablyVacated(ownership)
                    && _ownership.RetireNonRecoverable(ownership.Token))
                    _log?.Debug(
                        $"ActorSpawnService: non-recoverable readout cleared - created index {ownership.CreatedIndex} is vacated");
                else
                    _log?.Debug(
                        $"ActorSpawnService: non-recoverable record retained for readout (index {ownership.CreatedIndex})");
                continue;
            }
            if (ownership.State == SpawnOwnershipState.PendingCreate)
            {
                if (TryFinishPendingCreate(ownership))
                    deleted = true;
                else
                    _log?.Warning(
                        $"ActorSpawnService: Retaining pending-create record at index {ownership.CreatedIndex}");
                continue;
            }
            if (TryDelete(ownership))
                deleted = true;
            else
                _log?.Warning($"ActorSpawnService: Retaining pending actor at index {ownership.CreatedIndex}");
        }

        // Both callers (GPose exit, dispose) end the session the legacy
        // visibility overrides belong to.
        _legacyVisibility.Clear();

        if (deleted)
            _actorManager.RefreshActors();
    }

    public void Dispose()
    {
        _disposed = true;
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
