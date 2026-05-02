using System;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for managing actor skeletons.
/// </summary>
public interface ISkeletonService : IDisposable
{
    /// <summary>
    /// Gets or creates the skeleton for an actor.
    /// </summary>
    ISkeleton? GetSkeleton(IActor actor);

    /// <summary>
    /// Refreshes the skeleton for an actor.
    /// </summary>
    void RefreshSkeleton(IActor actor);

    /// <summary>
    /// Clears all cached skeletons.
    /// </summary>
    void ClearAll();
}
