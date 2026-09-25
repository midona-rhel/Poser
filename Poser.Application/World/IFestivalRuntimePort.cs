using Poser.Domain.Scene;
using System.Collections.Generic;

namespace Poser.Application.World;

/// <summary>
/// The eight engine festival slots (Brio's FestivalService). Changes are queued
/// and applied on a framework tick when the layout engine is between festival
/// transitions — applying one mid-transition is what corrupts the zone layout.
///
/// Unlike the time/weather holds, festivals ARE restored: the pre-override
/// slots are snapshotted on the first mutation and written back on GPose exit,
/// on disposal, and on demand.
/// </summary>
public interface IFestivalRuntimePort
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
