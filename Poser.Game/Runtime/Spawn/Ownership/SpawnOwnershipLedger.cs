using Poser.Core;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Domain.Identity;

namespace Poser.Game;

internal sealed class SpawnOwnershipLedger
{
    private readonly Dictionary<Guid, SpawnOwnershipRecord> _records = new();

    public IReadOnlyList<SpawnOwnershipRecord> Snapshot => _records.Values.ToArray();

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
