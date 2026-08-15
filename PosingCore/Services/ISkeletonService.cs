using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Slot-aware skeleton discovery and caching. One actor owns independently
/// replaceable slot skeletons; there is no cross-slot fallback.
/// </summary>
public interface ISkeletonService : IDisposable
{
    /// <summary>
    /// Gets or creates the actor's Character-slot skeleton.
    /// </summary>
    ISkeleton? GetSkeleton(IActor actor);

    /// <summary>
    /// Gets or creates one slot's skeleton; null when the slot is absent.
    /// </summary>
    ISkeleton? GetSkeleton(IActor actor, PoseSlot slot);

    /// <summary>
    /// Every currently present slot skeleton of the actor, rebuilding
    /// entries whose native binding changed and dropping vanished slots.
    /// </summary>
    IReadOnlyList<ISkeleton> GetSkeletons(IActor actor);

    /// <summary>
    /// Refreshes every cached slot skeleton of the actor.
    /// </summary>
    void RefreshSkeleton(IActor actor);

    /// <summary>
    /// Clears all cached skeletons.
    /// </summary>
    void ClearAll();
}
