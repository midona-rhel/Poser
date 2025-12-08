namespace Poser.Entities.Capabilities;

/// <summary>
/// Capability marker interface for entities that can be positioned in 3D space.
/// Implemented by: IActor, IBone
///
/// Note: Transform manipulation is done through services (IPosingService, IBonePosingService).
/// This interface marks entities that support transform operations.
///
/// The Transform property comes from IEntity, which ITransformable implementors must also implement.
/// </summary>
public interface ITransformable
{
    /// <summary>
    /// Whether to show a gizmo for this entity when selected.
    /// </summary>
    bool ShowGizmo { get; }
}
