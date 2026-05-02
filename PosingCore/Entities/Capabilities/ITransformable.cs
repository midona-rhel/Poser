namespace Poser.Entities.Capabilities;

/// <summary>
/// Capability marker interface for entities that can be positioned in 3D space.
/// Implemented by: IActor, IBone, LightEntity, VirtualCameraEntity
///
/// Note: Transform manipulation is typically done through services (IPosingService, IBonePosingService).
/// Check CanSetTransform to determine if direct Transform property assignment works.
///
/// The Transform property comes from IEntity, which ITransformable implementors must also implement.
/// </summary>
public interface ITransformable
{
    /// <summary>
    /// Whether to show a gizmo for this entity when selected.
    /// </summary>
    bool ShowGizmo { get; }

    /// <summary>
    /// Whether the Transform property setter actually applies changes.
    /// False for entities like actors (use IPosingService) or virtual bones (computed).
    /// True for lights, cameras, and bones.
    /// </summary>
    bool CanSetTransform { get; }
}
