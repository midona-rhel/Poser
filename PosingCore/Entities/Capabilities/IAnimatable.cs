namespace Poser.Entities.Capabilities;

/// <summary>
/// Capability marker interface for entities that can be animated.
/// Implemented by: IActor
///
/// Note: animation control runs through AnimationSession and the stable-id
/// IAnimationRuntimePort. This interface only marks which entities the
/// runtime will accept animation commands for.
/// </summary>
public interface IAnimatable
{
    /// <summary>
    /// Whether animation controls are available for this entity.
    /// Returns false for companions (minions, mounts) which have limited control.
    /// </summary>
    bool CanControlAnimation { get; }
}
