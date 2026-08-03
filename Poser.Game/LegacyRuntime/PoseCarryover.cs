using System;
using System.Collections.Generic;
using Poser.Core;
using Poser.Domain.Identity;

namespace Poser.Game;

/// <summary>
/// One slot's authored pose parked while its skeleton is being rebuilt.
/// <c>Pose</c> is already reduced to the carryover semantics (interactive
/// stacks only, rotation everywhere, position on the pose root) and holds no
/// reference to the skeleton instance it came from, so it can be seeded
/// straight into the replacement's store.
/// </summary>
internal sealed record CarryoverEntry(
    SkeletonPoseInfo Pose,
    Transform? ModelOverride,
    long CapturedAtTick);

/// <summary>
/// Short-lived parking lot for poses crossing a redraw, keyed by STABLE
/// identity (logical actor id + slot) because the actor address, the skeleton
/// instance and the draw object all change while the actor is rebuilt.
/// Entries expire so a rebuild that never completes — actor despawned mid
/// Penumbra reload, GPose torn down by the game — can never resurrect a pose
/// onto an unrelated later skeleton.
/// </summary>
internal sealed class PoseCarryoverStore
{
    /// <summary>Redraws settle in well under a second; the window only has to
    /// outlast the bounded skeleton retry pump (0.5s→5s backoff, 1s slot poll).</summary>
    private const long LifetimeMilliseconds = 30_000;

    private readonly Dictionary<(Guid LogicalId, PoseSlot Slot), CarryoverEntry> _entries = new();

    public void Park(Guid logicalId, PoseSlot slot, CarryoverEntry entry)
    {
        DropExpired(entry.CapturedAtTick);
        _entries[(logicalId, slot)] = entry;
    }

    /// <summary>Removes and returns the parked entry for a slot; null when
    /// nothing is parked or the parked entry has outlived its window.</summary>
    public CarryoverEntry? Take(Guid logicalId, PoseSlot slot)
    {
        // Fully qualified: the sibling Poser.Game.Environment namespace shadows
        // the System.Environment type inside this namespace.
        DropExpired(global::System.Environment.TickCount64);

        var key = (logicalId, slot);
        if (!_entries.TryGetValue(key, out var entry))
            return null;

        _entries.Remove(key);
        return entry;
    }

    public void Clear() => _entries.Clear();

    private void DropExpired(long now)
    {
        List<(Guid LogicalId, PoseSlot Slot)>? expired = null;
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.CapturedAtTick > LifetimeMilliseconds)
                (expired ??= new()).Add(key);
        }

        if (expired == null)
            return;

        foreach (var key in expired)
            _entries.Remove(key);
    }
}
