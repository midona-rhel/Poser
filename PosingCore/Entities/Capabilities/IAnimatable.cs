namespace Poser.Entities.Capabilities;

/// <summary>
/// Capability marker interface for entities that can be animated.
/// Implemented by: IActor
///
/// Note: Animation control is done through IAnimationService.
/// This interface marks entities that support animation operations.
/// </summary>
public interface IAnimatable
{
    /// <summary>
    /// Whether animation controls are available for this entity.
    /// Returns false for companions (minions, mounts) which have limited control.
    /// </summary>
    bool CanControlAnimation { get; }
}
