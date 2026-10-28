namespace Poser.Domain.Preferences;

/// <summary>
/// Selects the point around which a rotation gesture turns the selection.
/// </summary>
public enum RotationPivot
{
    /// <summary>Normal in-place rotation (no orbit).</summary>
    Self,
    /// <summary>Use the primary bone's parent position at the start of the gesture.</summary>
    Parent,
}
