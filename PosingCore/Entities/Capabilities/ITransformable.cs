namespace Poser.Entities.Capabilities;

/// <summary>
/// Transitional capability marker for actors, concrete bones, and virtual
/// bone groups that can participate in transform presentation.
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
    /// False for actors (use IPosingService) and virtual bones (computed).
    /// True only for concrete bones.
    /// </summary>
    bool CanSetTransform { get; }
}
