using Poser.Entities;

namespace Poser.Entities.Capabilities;

/// <summary>
/// Capability interface for entities that own a skeleton.
/// Implemented by: IActor
/// </summary>
public interface ISkeletonOwner
{
    /// <summary>
    /// The skeleton owned by this entity, or null if not available.
    /// </summary>
    ISkeleton? Skeleton { get; }

    /// <summary>
    /// Whether the skeleton is currently loaded and available.
    /// </summary>
    bool HasSkeleton { get; }
}
