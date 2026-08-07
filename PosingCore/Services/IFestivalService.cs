using System.Collections.Generic;

namespace Poser.Services;

/// <summary>One engine festival slot: id 0 means the slot is empty.</summary>
public readonly record struct ActiveFestival(uint Id, ushort Phase);

/// <summary>A phase the reference data names for a festival.</summary>
public sealed record FestivalPhaseInfo(int Id, string Name);

/// <summary>
/// A festival row joined with the curated reference data. A row the data does
/// not name is still offered, flagged <see cref="Unknown"/>; a row known to
/// break the game in some places is flagged <see cref="Unsafe"/>.
/// </summary>
public sealed record FestivalEntry(
    uint Id,
    string Name,
    bool Unknown,
    bool Unsafe,
    IReadOnlyList<FestivalPhaseInfo> KnownPhases);

/// <summary>
/// The eight engine festival slots (Brio's FestivalService). Changes are queued
/// and applied on a framework tick when the layout engine is between festival
/// transitions — applying one mid-transition is what corrupts the zone layout.
///
/// Unlike the time/weather holds, festivals ARE restored: the pre-override
/// slots are snapshotted on the first mutation and written back on GPose exit,
/// on disposal, and on demand.
/// </summary>
public interface IFestivalService
{
    /// <summary>Engine slot count.</summary>
    const int MaxFestivals = 8;

    /// <summary>All <see cref="MaxFestivals"/> slots in engine order, empty
    /// ones included, so slot identity survives the boundary.</summary>
    IReadOnlyList<ActiveFestival> ActiveFestivals { get; }

    /// <summary>Every festival row, keyed by id.</summary>
    IReadOnlyDictionary<uint, FestivalEntry> FestivalList { get; }

    /// <summary>At least one empty slot — <see cref="Add"/> fails without one.</summary>
    bool HasFreeSlot { get; }

    /// <summary>The original slots have been snapshotted, i.e. there is
    /// something to reset.</summary>
    bool HasOverride { get; }

    /// <summary>Festivals are only editable inside GPose.</summary>
    bool CanModify { get; }

    /// <summary>Queues a festival into the first empty slot. False when no slot
    /// is free or the festival is excluded where the player is standing.</summary>
    bool Add(uint id, ushort phase = 1);

    /// <summary>Queues the removal of a festival. False when it is not active
    /// or is excluded where the player is standing.</summary>
    bool Remove(uint id);

    /// <summary>Queues a phase change, adding the festival when it is not
    /// already active.</summary>
    bool ChangePhase(uint id, ushort phase);

    /// <summary>Queues the pre-override slots and drops the snapshot.</summary>
    void Reset();
}
