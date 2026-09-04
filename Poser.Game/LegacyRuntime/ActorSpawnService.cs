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
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Game.Integration;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Exact native identity of one client-object slot occupant.
/// <see cref="Index"/> is the ClientObjectManager slot, never
/// <c>GameObject.ObjectIndex</c> — see
/// <c>docs/architecture/posing-runtime.md</c> for the index-space rule.
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

    /// <summary>Whether the spawn assigned this clone a Penumbra collection.
    /// It is ownership, not appearance state: only a record that took the
    /// assignment is allowed to release one, so a foreign assignment on a
    /// reused identifier is never deleted on our behalf.</summary>
    public bool CollectionAssigned { get; private set; }

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

    public void MarkCollectionAssigned() => CollectionAssigned = true;
    public void MarkCollectionReleased() => CollectionAssigned = false;

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
/// One client object as ClientObjectManager reports it. It carries BOTH
/// numbers the native object has — the slot it was resolved by and its global
/// <see cref="ObjectIndex"/> — because they genuinely differ (see
/// <c>docs/architecture/posing-runtime.md</c>), so a test ClientObjectManager
/// reproduces the difference and picking the wrong one fails a test instead of
/// a live spawn.
/// </summary>
internal readonly record struct ClientObjectSnapshot(
    nint Address,
    ulong EntityId,
    ushort ObjectIndex);

/// <summary>
/// The four ClientObjectManager entry points the spawn transaction needs.
/// <see cref="CreateBattleCharacter"/> is declared WITHOUT default arguments on
/// purpose: the native signature is
/// <c>CreateBattleCharacter(uint index = uint.MaxValue, byte param = 0)</c>,
/// where a lone <c>byte</c> binds silently to <c>index</c> — the slot to build
/// in — instead of the companion flag. Both arguments are mandatory here, so
/// that mistake cannot compile.
/// </summary>
internal interface IClientObjectManagerNative
{
    uint CreateBattleCharacter(uint index, byte param);
    ClientObjectSnapshot? GetObjectByIndex(ushort index);
    uint GetIndexByObject(nint address);
    void DeleteObjectByIndex(ushort index, byte param);
}

/// <summary>
/// Client-object identity and lifetime bookkeeping: the production logic
/// behind the adapter's index members, kept off <c>unsafe</c> so tests run it
/// against a ClientObjectManager whose two index spaces really differ.
/// </summary>
internal sealed class SpawnClientObjects
{
    public const uint NoIndex = 0xFFFFFFFF;

    /// <summary>Let the game pick the slot. Naming one is how the clone ended
    /// up aimed at slot 0 — the GPose primary.</summary>
    private const uint NextAvailableSlot = uint.MaxValue;

    private readonly IClientObjectManagerNative _com;

    public SpawnClientObjects(IClientObjectManagerNative com) => _com = com;

    /// <summary>Destruction bookkeeping; the real adapter feeds it from the
    /// Character finalize hook.</summary>
    public SpawnLifetimeStamps Stamps { get; } = new();

    public uint CreateBattleCharacter(byte reserveCompanionSlot) =>
        _com.CreateBattleCharacter(NextAvailableSlot, reserveCompanionSlot);

    public ulong IndexDestructionStamp(ushort index) => Stamps.IndexStampFor(index);

    public SpawnNativeDescriptor? ResolveByIndex(ushort index)
    {
        if (_com.GetObjectByIndex(index) is not { } native)
            return null;
        return new SpawnNativeDescriptor(
            index,
            native.Address,
            native.EntityId,
            Stamps.StampFor(native.Address));
    }

    public SpawnNativeDescriptor? ResolveActor(nint address)
    {
        if (address == nint.Zero)
            return null;
        var index = _com.GetIndexByObject(address);
        if (index == NoIndex)
            return null;
        var current = ResolveByIndex((ushort)index);
        return current is { } descriptor && descriptor.Address == address
            ? descriptor
            : null;
    }

    public bool DeleteExact(SpawnNativeDescriptor descriptor)
    {
        var current = ResolveByIndex(descriptor.Index);
        if (current is null || current.Value != descriptor)
            return false;
        _com.DeleteObjectByIndex(descriptor.Index, 0);
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

    /// <summary>Writes the character's alpha. This is how an actor is HIDDEN;
    /// see <see cref="ActorSpawnNativeAdapter.SetAlpha"/> for why it is not
    /// <see cref="SetDrawState"/>.</summary>
    bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha);

    bool? IsReadyToDraw(SpawnNativeDescriptor descriptor);

    /// <summary>Copies the equipment visibility flags — weapons, headgear,
    /// visor, Viera ears — from one character onto another. The seed copy
    /// carries the equipment but not these flags: a duplicate showed the
    /// weapon on its back while the source hid it (2026-09-02).</summary>
    bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target);

    /// <summary>Seeds the target's DrawData customize and equipment from
    /// the source's DRAWN model (Human.Customize, Human equipment models).
    /// A sync plugin or a locked Glamourer state writes the draw object and
    /// leaves DrawData at the game's values, so the game's own copy drew a
    /// vanilla character next to a modded one (2026-09-02, Valya).</summary>
    bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target);
    bool HasCompanionSlot(SpawnNativeDescriptor descriptor);
    /// <summary>Reads the slot. False when the descriptor no longer
    /// revalidates — an unreadable actor is NOT an empty slot, and only the
    /// empty slot may be written over. On true, a null
    /// <paramref name="attachment"/> is the empty slot.</summary>
    bool TryReadCompanion(
        SpawnNativeDescriptor descriptor,
        out CompanionAttachment? attachment);

    /// <summary>The attached child object's address; zero when the slot is
    /// empty or the descriptor no longer revalidates. It is the companion's
    /// own BODY — the attachment ids alone name a sheet row, not a posable
    /// object.</summary>
    nint ReadCompanionAddress(SpawnNativeDescriptor descriptor);

    bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id);
    bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want);
    bool EnableCompanionDraw(SpawnNativeDescriptor descriptor);
    int? ReadModelCharaId(SpawnNativeDescriptor descriptor);
    bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId);
}

/// <summary>The live ClientObjectManager. The only place a client object's
/// global <c>ObjectIndex</c> is read.</summary>
internal unsafe sealed class ClientObjectManagerNative : IClientObjectManagerNative
{
    public bool IsAvailable => ClientObjectManager.Instance() is not null;

    public uint CreateBattleCharacter(uint index, byte param)
    {
        var com = ClientObjectManager.Instance();
        return com is null
            ? SpawnClientObjects.NoIndex
            : com->CreateBattleCharacter(index, param);
    }

    public ClientObjectSnapshot? GetObjectByIndex(ushort index)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return null;
        var native = com->GetObjectByIndex(index);
        if (native is null)
            return null;
        return new ClientObjectSnapshot(
            (nint)native, native->EntityId, native->ObjectIndex);
    }

    public uint GetIndexByObject(nint address)
    {
        var com = ClientObjectManager.Instance();
        if (com is null || address == nint.Zero)
            return SpawnClientObjects.NoIndex;
        return com->GetIndexByObject((GameObject*)address);
    }

    public void DeleteObjectByIndex(ushort index, byte param)
    {
        var com = ClientObjectManager.Instance();
        if (com is not null)
            com->DeleteObjectByIndex(index, param);
    }
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

    private readonly ClientObjectManagerNative _com = new();
    private readonly SpawnClientObjects _objects;
    private readonly Hook<CharacterFinalizeDelegate>? _finalizeHook;
    private readonly string? _lifetimeAuthorityDetail;

    public ActorSpawnNativeAdapter(
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog? log)
    {
        _objects = new SpawnClientObjects(_com);
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

    public bool IsAvailable => _com.IsAvailable;
    public bool IsLifetimeAuthoritative => _finalizeHook is not null;
    public string? LifetimeAuthorityDetail => _lifetimeAuthorityDetail;

    private nint CharacterFinalizeDetour(Character* chara)
    {
        try
        {
            var slot = _com.GetIndexByObject((nint)chara);
            _objects.Stamps.NoteDestroyed(
                (nint)chara,
                slot == SpawnClientObjects.NoIndex ? null : (ushort)slot);
        }
        catch
        {
            // Never fault the native destructor.
        }
        return _finalizeHook!.Original(chara);
    }

    public uint CreateBattleCharacter(byte reserveCompanionSlot) =>
        _objects.CreateBattleCharacter(reserveCompanionSlot);

    public ulong IndexDestructionStamp(ushort index) =>
        _objects.IndexDestructionStamp(index);

    public SpawnNativeDescriptor? ResolveByIndex(ushort index) =>
        _objects.ResolveByIndex(index);

    public SpawnNativeDescriptor? ResolveActor(nint address) =>
        _objects.ResolveActor(address);

    public bool DeleteExact(SpawnNativeDescriptor descriptor) =>
        _objects.DeleteExact(descriptor);

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

    /// <summary>
    /// Writes the character's alpha, which is how an actor is HIDDEN.
    ///
    /// <para>Not <see cref="SetDrawState"/>: <c>DisableDraw</c> tears the draw
    /// object down, and the skeleton — with the user's whole pose on it — goes
    /// with it, so re-showing rebuilt the actor standing in its animation's
    /// pose. Both references hide by fading instead, and both land on this
    /// same field: Brio writes <c>ExtendedAppearance.Transparency</c>
    /// (Capabilities/Actor/ActorAppearanceCapability.cs ToggleHide), Ktisis
    /// writes <c>CharacterEx-&gt;Opacity</c> (Scene/Entities/Game/
    /// ActorEntity.cs IsHidden). The draw object survives, so the pose does.
    /// </para>
    ///
    /// <para>The field's provenance is stated once, in
    /// <c>PresentationRuntimePort</c> (Brio's <c>Character.Alpha</c>,
    /// CS-named); this is the same field the Opacity slider drives, which is
    /// exactly the relationship both references have between their hide verb
    /// and their transparency control.</para>
    /// </summary>
    public bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        character->Alpha = Math.Clamp(alpha, 0f, 1f);
        return true;
    }

    public bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target)
    {
        var from = (Character*)Revalidate(source);
        var to = (Character*)Revalidate(target);
        if (from == null || to == null)
            return false;
        var drawn = from->GameObject.DrawObject;
        if (drawn == null
            || drawn->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase
            || ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawn)->GetModelType()
                != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
            return false;
        var human = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)drawn;
        to->DrawData.CustomizeData = human->Customize;
        var models = human->EquipmentModels;
        var slots = to->DrawData.EquipmentModelIds;
        for (int i = 0; i < models.Length && i < slots.Length; i++)
            slots[i] = models[i];
        // Facewear: its own two slots, not among the ten.
        var glasses = from->DrawData.GlassesIds;
        for (int i = 0; i < glasses.Length; i++)
            to->DrawData.SetGlasses(i, glasses[i]);
        var toDrawn = to->GameObject.DrawObject;
        if (toDrawn != null
            && toDrawn->Object.GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase
            && ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)toDrawn)->GetModelType()
                == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
        {
            // Already drawn (the once-posable pass): the drawn glasses models
            // straight across, the way the sync plugin set them.
            var toHuman = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)toDrawn;
            var sourceGlasses = human->GlassesModels;
            for (uint i = 0; i < (uint)sourceGlasses.Length; i++)
            {
                var model = sourceGlasses[(int)i];
                toHuman->SetGlassesSlotModel(i, &model);
            }
        }
        return true;
    }

    public bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target)
    {
        var from = (Character*)Revalidate(source);
        var to = (Character*)Revalidate(target);
        if (from == null || to == null)
            return false;
        to->DrawData.HideWeapons(from->DrawData.IsWeaponHidden);
        to->DrawData.HideHeadgear(0, from->DrawData.IsHatHidden);
        to->DrawData.SetVisor(from->DrawData.IsVisorToggled);
        to->DrawData.HideVieraEars(from->DrawData.VieraEarsHidden);
        // The two flag bytes wholesale (+0x23E/+0x23F: the toggles above and
        // the rest — facewear visibility among them).
        *((byte*)&to->DrawData + 0x23E) = *((byte*)&from->DrawData + 0x23E);
        *((byte*)&to->DrawData + 0x23F) = *((byte*)&from->DrawData + 0x23F);
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

    public nint ReadCompanionAddress(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null || character->ChildObject == null)
            return nint.Zero;
        // The child's GameObject is what the object table lists it by, which
        // is what an IActor's Address is.
        return (nint)(&character->ChildObject->GameObject);
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
        SpawnOwnershipRecord ownership,
        ISpawnCollectionPort? collections = null,
        IPluginLog? log = null)
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
            {
                if (ownership.CollectionAssigned)
                    log?.Warning(
                        $"ActorSpawnService: the clone at index {ownership.CreatedIndex} was already gone, so its Penumbra collection assignment could not be released");
                return ledger.TryRetire(ownership);
            }
            if (current.Value != ownership.Descriptor.Value)
                return false;
            // Released against the PROVEN identity and on the last frame it
            // still exists: Penumbra keys the assignment on the object's own
            // identifier, so after the delete there is nothing left to name.
            ReleaseCollection(ownership, collections, log);
            if (!native.DeleteExact(ownership.Descriptor.Value))
                return false;
            return ledger.TryRetire(ownership);
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseCollection(
        SpawnOwnershipRecord ownership,
        ISpawnCollectionPort? collections,
        IPluginLog? log)
    {
        if (!ownership.CollectionAssigned || collections is null)
            return;
        try
        {
            var released = collections.ReleaseCollection(ownership.Descriptor!.Value.Address);
            if (released.Success)
                ownership.MarkCollectionReleased();
            else
                log?.Warning(
                    $"ActorSpawnService: the clone's Penumbra collection assignment was not released: {released.Detail}");
        }
        catch (Exception ex)
        {
            // A failing external call never blocks the delete: the object
            // has to go either way, and the leftover assignment is named
            // after the clone, not after anything the user owns.
            log?.Warning(
                $"ActorSpawnService: releasing the clone's Penumbra collection assignment failed: {ex.Message}");
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
    private const int CompanionTransitionTimeoutMs = 1000;
    // Ready/empty can become observable before a mount/ornament transition
    // finishes resetting its owner. Keep the exact state stable for a short
    // interval while the pre-transition placement override is reasserted.
    private const int CompanionSettleMs = 100;

    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IPosingService? _posing;
    private readonly IPluginLog? _log;
    private readonly IFramework? _framework;
    private readonly Func<nint> _localPlayerAddress;

    /// <summary>Address of one object-table slot, re-read on demand. Held as a
    /// function for the same reason <see cref="_localPlayerAddress"/> is: the
    /// service is constructed in tests without a live table.</summary>
    private readonly Func<int, nint> _objectAddressAt;
    private readonly Func<nint, EntityId?> _expectedWrapperIdentity;
    private readonly Func<long> _clock;
    private readonly bool _ownsAdapter;

    private readonly IActorSpawnNativeAdapter _native;
    private readonly ISpawnCollectionPort? _collections;
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
        IGameInteropProvider hooking,
        ISpawnCollectionPort collections,
        IPosingService posing)
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
            posing: posing)
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
        IPosingService? posing = null)
    {
        _framework = framework;
        _collections = collections;
        _posing = posing;
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _log = log;
        _native = native;
        _localPlayerAddress = localPlayerAddress;
        // Fail closed here too: with no way to read the object table, no
        // address is inside the GPose range and nothing pre-existing deletes.
        _objectAddressAt = objectAddressAt ?? (_ => nint.Zero);
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
        // A new actor is not a copy of the player as far as mods go: it
        // wears the player's collection live, not a snapshot of it.
        return SpawnCloneFrom(localPlayer, reserveCompanionSlot, inheritSource: false);
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

    /// <summary>Brio's AddFromWorld: the overworld character ITSELF joins
    /// GPose — the same body, no copy — and the scene lists it by
    /// reference for as long as GPose lasts. Nothing is written to it
    /// here; the registration is the game's own call.</summary>
    public IActor? AdoptFromWorld(nint address) => AdoptFromWorldSource(address);

    internal IActor? AdoptFromWorldSource(nint sourceAddress)
    {
        if (!OnOwnerThread || sourceAddress == nint.Zero || !_gPoseService.IsGPosing)
            return null;
        AddCharacterToGPose((Character*)sourceAddress);
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
        var actor = SpawnCloneFrom(
            localPlayer,
            reserveCompanionSlot: false,
            inheritSource: false,
            modelCharaId: entry.ModelCharaId,
            // The game name stays a Poser name: Penumbra identifies a
            // player-kind object by a two-word capitalized name and answered
            // InvalidIdentifier (16) for "Morbol seedling", so the actor got
            // no collection (2026-09-03). The label is the nickname instead.
            name: null,
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
        bool inheritSource = true,
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
            // Penumbra cannot place the copy on the frame it is created
            // (CollectionMissing, 00:14) and can one tick later (00:2x): the
            // inherit runs next tick, still ahead of the draw. The flags
            // copy is a plain field write and lands now.
            if (sourceAddress != nint.Zero
                && _native.ResolveActor(sourceAddress) is { } flagSource)
            {
                _native.CopyDrawnAppearance(flagSource, descriptor.Value);
                _native.CopyEquipmentVisibility(flagSource, descriptor.Value);
            }
            var seeded = descriptor.Value;
            if (_framework is null)
                InheritSourceCollection(ownership, sourceAddress, seeded, inheritSource);
            else
                _framework.RunOnTick(() =>
                {
                    if (_disposed)
                        return;
                    InheritSourceCollection(ownership, sourceAddress, seeded, inheritSource);
                }, delayTicks: 1);
            DrawWhenReady(ownership, descriptor.Value);

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
        // The tick runs every frame until a terminal outcome, so a fault that
        // reproduces every frame is said once — but keyed by type and message,
        // because a NEW fault inside the window is news, not the same line.
        string? lastFault = null;
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
                var fault = $"{ex.GetType().FullName}: {ex.Message}";
                if (fault != lastFault)
                {
                    lastFault = fault;
                    _log?.Warning(
                        $"ActorSpawnService: pending-create recovery for index {ownership.CreatedIndex} faulted: {fault}");
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
        var result = SpawnOwnershipCleanup.TryDelete(
            _ownership, _native, ownership, _collections, _log);
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

        // Brio registers the new body with GPose BEFORE the appearance copy
        // (ActorSpawnService.cs:325-327): the second copy exists to trigger a
        // redraw for Penumbra/Glamourer, and those tools decide what to apply
        // from what the object IS at that moment.
        EnsureCurrent(ownership);
        AddCharacterToGPose(newCharacter);

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

        // The draw is NOT started here: see DrawWhenReady, which the spawn
        // transaction runs once the mutations are done.
    }

    /// <summary>
    /// The clone wears the source's MODS, not just its raw appearance.
    /// Penumbra resolves a GPose actor through the parent index its
    /// CopyCharacter hook recorded (Penumbra CutsceneService.cs:123-130), and
    /// the second, self-directed CharacterSetup copy above points that parent
    /// at the clone itself — so the clone resolves under its own name and
    /// inherits nothing. Brio's clone path never repairs that (its
    /// ActorSpawnService.cs:108-172 makes no Penumbra call at all) and leaves
    /// the user to pick a collection by hand afterwards
    /// (Brio ActorAppearanceCapability.cs:210-235); Poser copies the source's
    /// effective collection instead.
    ///
    /// It runs INSIDE the deferred-draw window, before the draw object is
    /// ever built, which is the same window the copy itself needs. A refusal
    /// is reported and never fails the spawn: an unmodded clone is still a
    /// clone. The self-copy is also what makes this safe — without it the
    /// assignment would land on the SOURCE's identifier and rewrite the
    /// user's own character collection.
    /// </summary>
    private void InheritSourceCollection(
        SpawnOwnershipRecord ownership,
        nint sourceAddress,
        SpawnNativeDescriptor descriptor,
        bool inheritSource)
    {
        if (_collections is null || sourceAddress == nint.Zero)
            return;
        EnsureCurrent(ownership);
        IntegrationPortResult result;
        try
        {
            // A duplicate carries a snapshot of its source's mods (a locked
            // or synced source cannot be assigned by name); a plain spawn
            // simply wears the player's collection.
            result = inheritSource
                ? _collections.InheritCollection(sourceAddress, descriptor.Address)
                : _collections.AssignPlayerCollection(descriptor.Address);
        }
        catch (Exception ex)
        {
            _log?.Warning(
                $"ActorSpawnService: the clone could not inherit the source's Penumbra collection: {ex.Message}");
            return;
        }
        if (result.Success)
            ownership.MarkCollectionAssigned();
        else
            _log?.Warning(
                $"ActorSpawnService: the clone could not inherit the source's Penumbra collection: {result.Detail}");
    }

    /// <summary>
    /// Brio's <c>ActorRedrawService.DrawWhenReady</c> (ActorSpawnService.cs:156
    /// → ActorRedrawService.cs:99-110): skip two frames, then hold the draw
    /// until <c>IsReadyToDraw</c>, and only then enable it. Drawing in the same
    /// tick as the appearance copy builds the draw object from whatever was
    /// still resident and renders the BASE appearance instead of the source's —
    /// the skipped frames are also the window Penumbra/Glamourer need to react
    /// to the copy's redraw before the object is built. Without a framework
    /// there is no way to defer, so the draw is started immediately.
    /// </summary>
    private void DrawWhenReady(
        SpawnOwnershipRecord ownership,
        SpawnNativeDescriptor descriptor)
    {
        if (_framework is null)
        {
            _native.SetDrawState(descriptor, true);
            return;
        }
        PollUntil(
            ownership,
            descriptor,
            () => _native.IsReadyToDraw(descriptor) == true,
            () => _native.SetDrawState(descriptor, true),
            timeoutMs: 2000,
            what: $"clone draw at index {descriptor.Index}",
            skipFrames: 2);
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
        // Simple naming: "Poser One", "Poser Two", etc. Slot 0 has no word
        // (and is a real slot the game can hand back), so it takes the number.
        string[] ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
                         "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };

        if (index > 0 && index < ones.Length)
            return $"Poser {ones[index]}";

        return $"Poser {index}";
    }

    public bool DestroyActor(IActor actor)
    {
        if (actor.Address == nint.Zero || !OnOwnerThread)
            return false;
        // An adopted body is the world's: Destroy seats it back where it
        // was taken and lets it go.
        if (_actorManager.IsAdopted(actor))
        {
            _actorManager.ReleaseWorldActor(actor.Address);
            return true;
        }

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

    /// <summary>
    /// The GPose object table's range. Brio gates its own scene destruction on
    /// exactly this and nothing else — <c>DestroyAll</c> walks
    /// <c>_objectTable[GPoseStart..GPoseEnd]</c> and hands each object to
    /// <c>DestroyObject</c> (<c>Brio/Game/Actor/ActorSpawnService.cs:175-183</c>,
    /// <c>ActorTableHelpers.cs:5-8</c>). 200 is the GPose primary and is
    /// deliberately outside the range, which is what makes "clear the scene"
    /// safe to mean everything in it.
    /// </summary>
    private const int GPoseTableStart = 201;
    private const int GPoseTableEnd = 439;

    /// <summary>
    /// Removes exactly one actor from the temporary GPose object table.
    ///
    /// <para>An actor Poser owns keeps the stronger create-time identity and
    /// collection-release contract — it routes to <see cref="DestroyActor"/>
    /// rather than being downgraded to a scene scan because the caller used
    /// the general verb.</para>
    ///
    /// <para>THE GATE, and where it lives. Brio range-checks the OBJECT TABLE
    /// index at the enumeration site and never range-checks the
    /// ClientObjectManager slot it ultimately deletes by
    /// (<c>com-&gt;GetIndexByObject</c> → <c>DeleteObjectByIndex</c>, guarded
    /// only against the 0xFFFFFFFF sentinel). Those are two DIFFERENT index
    /// spaces, so a 201-439 test on the manager slot would be checking the
    /// wrong number and refusing valid deletes. This states the real gate in
    /// the real space: the actor must still be standing in the GPose table
    /// range right now. It does not lean on the fact that
    /// <c>ActorManager</c> happens to scan the same range — a gate inherited
    /// by assumption widens silently the day that scan changes.</para>
    ///
    /// <para>Refuses the local/GPose primary, companion bodies, stale or
    /// non-root wrappers, and anything whose typed descriptor no longer
    /// resolves.</para>
    /// </summary>
    public bool RemoveActorFromScene(IActor actor)
    {
        if (actor.Address == nint.Zero || !OnOwnerThread)
            return false;

        try
        {
            // An adopted body is released: seated back where it was taken
            // and forgotten by the scene; the world keeps it.
            if (_actorManager.IsAdopted(actor))
            {
                _actorManager.ReleaseWorldActor(actor.Address);
                return true;
            }
            if (_ownership.TryGetBound(actor, out _))
                return DestroyActor(actor);

            if (RemovalRefusal(actor) is { } refusal)
            {
                _log?.Warning($"ActorSpawnService: {refusal}");
                return false;
            }

            var descriptor = _native.ResolveActor(actor.Address);
            if (descriptor is not { } current)
            {
                _log?.Warning(
                    "ActorSpawnService: Refused to remove actor without a current typed scene descriptor");
                return false;
            }

            // DeleteExact re-reads the typed descriptor immediately before
            // invoking ClientObjectManager.DeleteObjectByIndex(index, 0).
            if (!_native.DeleteExact(current))
                return false;

            _log?.Debug(
                $"ActorSpawnService: Removed GPose actor at index {current.Index}");
            _actorManager.RefreshActors();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error(
                $"ActorSpawnService: Failed to remove actor from scene: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The removal gates, read-only, each with the reason a user can act on.
    /// Null means removal would be admitted right now. The UI uses this to
    /// decide whether to offer the verb at all; the mutation re-runs it, so
    /// the answer can never be stale by more than the click.
    /// </summary>
    public string? RemovalRefusal(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return "The actor is no longer in the scene.";
        // An adopted body is released, not destroyed: Destroy is offered.
        if (_actorManager.IsAdopted(actor))
            return null;

        // A Poser-owned actor is always removable: it routes to the
        // stronger owned-teardown path, not the scene-table delete.
        if (_ownership.TryGetBound(actor, out _))
            return null;

        // No local-player exception, deliberately: Brio's CanDestroy admits
        // every actor in its container including your own GPose clone
        // (Brio/Capabilities/Actor/ActorLifetimeCapability.cs:88), and its
        // ClearAll deletes the whole GPose table. The clone is a temporary
        // copy; the overworld character is untouched by deleting it.
        if (actor.ActorKind is ActorKind.Companion or ActorKind.Mount
            or ActorKind.Ornament)
            return "Companions are removed by detaching them from their " +
                "owner, not from the scene.";

        // The wrapper check is the root/ownership boundary: it excludes
        // auxiliary registrations and an old wrapper after a refresh even
        // when a native address happens to be reused. A caller cannot
        // manufacture a wrapper with a copied address and turn that stale
        // view into permission to delete a scene slot.
        EntityId? expectedIdentity;
        try
        {
            expectedIdentity = _expectedWrapperIdentity(actor.Address);
        }
        catch
        {
            expectedIdentity = null;
        }
        if (expectedIdentity is not { } expected
            || expected != actor.Id
            || !_actorManager.Actors.Any(candidate =>
                ReferenceEquals(candidate, actor)
                && candidate.Id == expected
                && candidate.Address == actor.Address))
            return "The actor's identity is stale; it may have just been " +
                "replaced. Try again.";

        // Brio's gate, in Brio's index space, re-read now.
        if (!InGPoseTable(actor.Address))
            return "The actor is not part of the GPose scene.";

        return null;
    }

    /// <summary>Whether this address is currently a GPose-table object.
    /// Re-read at the write, never cached.</summary>
    private bool InGPoseTable(nint address)
    {
        for (int index = GPoseTableStart; index <= GPoseTableEnd; index++)
        {
            if (_objectAddressAt(index) == address)
                return true;
        }
        return false;
    }

    public bool CopyDrawnAppearance(IActor source, IActor target)
    {
        if (!OnOwnerThread || source.Address == nint.Zero || target.Address == nint.Zero)
            return false;
        try
        {
            if (_native.ResolveActor(source.Address) is not { } from
                || !TryResolveActorForOperation(target, out var to, out _))
                return false;
            return _native.CopyDrawnAppearance(from, to);
        }
        catch (Exception ex)
        {
            _log?.Warning($"ActorSpawnService: the drawn appearance could not be copied: {ex.Message}");
            return false;
        }
    }

    public bool CopyEquipmentVisibility(IActor source, IActor target)
    {
        if (!OnOwnerThread || source.Address == nint.Zero || target.Address == nint.Zero)
            return false;
        try
        {
            if (_native.ResolveActor(source.Address) is not { } from
                || !TryResolveActorForOperation(target, out var to, out _))
                return false;
            return _native.CopyEquipmentVisibility(from, to);
        }
        catch (Exception ex)
        {
            _log?.Warning($"ActorSpawnService: equipment visibility could not be copied: {ex.Message}");
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
            // Fade, never tear down — see IActorSpawnNativeAdapter.SetAlpha.
            // The remembered flag below stays the record of what the USER
            // asked for; the alpha is only how the game is told.
            if (!_native.SetAlpha(descriptor, visible ? 1f : 0f))
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

        if (existing is null && container is null)
            return true;

        if (_posing is null)
        {
            _log?.Warning(
                "ActorSpawnService: companion change skipped - owner placement cannot be preserved");
            return false;
        }
        var placement = _posing.GetEffectiveTransform(owner);
        if (!TryReassertOwnerPlacement(owner, placement))
            return false;

        if (existing is { } attached)
        {
            if (!_native.WriteCompanion(descriptor, attached.Kind, 0)
                || !TryReassertOwnerPlacement(owner, placement))
                return false;
        }
        if (container is not { } want)
        {
            PollForCompanionTransition(
                ownership,
                descriptor,
                owner,
                placement,
                () => _native.TryReadCompanion(descriptor, out var current)
                    && current is null,
                onSettled: null,
                what: "companion detach");
            return true;
        }

        if (!_native.WriteCompanion(descriptor, want.Kind, (short)want.Id)
            || !TryReassertOwnerPlacement(owner, placement))
            return false;

        PollForCompanionTransition(
            ownership,
            descriptor,
            owner,
            placement,
            () => _native.IsCompanionReady(descriptor, want),
            () => _native.EnableCompanionDraw(descriptor),
            what: $"companion {want.Kind} {want.Id}");

        return true;
    }

    private void PollForCompanionTransition(
        SpawnOwnershipRecord? ownership,
        SpawnNativeDescriptor descriptor,
        IActor owner,
        Transform placement,
        Func<bool> exactState,
        Action? onSettled,
        string what)
    {
        long? stableSince = null;
        PollUntil(
            ownership,
            descriptor,
            () =>
            {
                if (_posing!.GetTransformOverride(owner) is not { } armed
                    || armed != placement)
                {
                    stableSince = null;
                    return false;
                }
                if (!exactState())
                {
                    stableSince = null;
                    return false;
                }

                var now = _clock();
                stableSince ??= now;
                return now - stableSince.Value >= CompanionSettleMs;
            },
            () =>
            {
                if (TryReassertOwnerPlacement(owner, placement))
                    onSettled?.Invoke();
            },
            CompanionTransitionTimeoutMs,
            what,
            skipFrames: 1,
            onTick: () => _posing!.SetTransformOverride(owner, placement));
    }

    private bool TryReassertOwnerPlacement(
        IActor owner,
        Transform placement)
    {
        _posing!.SetTransformOverride(owner, placement);
        if (_posing.GetTransformOverride(owner) is { } armed
            && armed == placement)
            return true;

        _log?.Warning(
            "ActorSpawnService: companion transition could not preserve owner placement");
        return false;
    }

    public void DestroyCompanion(IActor owner)
    {
        _ = SetCompanion(owner, null);
    }

    public CompanionAttachment? GetCompanionInfo(IActor owner)
    {
        if (!OnOwnerThread)
            return null;
        if (!TryResolveActorForOperation(owner, out var descriptor, out _))
            return null;
        return _native.TryReadCompanion(descriptor, out var info) ? info : null;
    }

    public IActor? GetCompanionActor(IActor owner)
    {
        if (!OnOwnerThread)
            return null;
        if (!TryResolveActorForOperation(owner, out var descriptor, out _))
            return null;
        var address = _native.ReadCompanionAddress(descriptor);
        if (address == nint.Zero)
            return null;
        foreach (var actor in _actorManager.Actors)
        {
            if (actor.Address == address)
                return actor;
        }
        // The child object exists natively but has no wrapper yet; a caller
        // that needs the body waits rather than being handed the owner.
        return null;
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
    ///
    /// <paramref name="skipFrames"/> is Brio's <c>dontStartFor</c>: the first
    /// frames after a native mutation can answer a readiness question with the
    /// state that preceded it, so a condition that must not be believed too
    /// early skips them outright rather than trusting the first answer.
    /// <paramref name="onTick"/> runs only after the exact lifetime check and
    /// before both the skip and condition, so guarded state can be enforced
    /// even during skipped frames.
    /// </summary>
    private void PollUntil(
        SpawnOwnershipRecord? ownership,
        SpawnNativeDescriptor lifetime,
        Func<bool> condition,
        Action onSatisfied,
        int timeoutMs,
        string what,
        int skipFrames = 0,
        Action? onTick = null)
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
        var remainingSkips = skipFrames;
        void Tick(IFramework fw)
        {
            try
            {
                if (!IsCallbackCurrent(token, lifetime))
                {
                    _framework.Update -= Tick;
                    return;
                }
                onTick?.Invoke();
                if (remainingSkips > 0)
                {
                    // Still inside the window where the condition would answer
                    // about the pre-mutation state; the deadline keeps running.
                    remainingSkips--;
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
