namespace Poser.Core;

/// <summary>
/// The single visible rotation pivot choice (toolbar, beside Local/World).
/// Parent routes through the clean transform gesture with a pivot frozen at
/// Begin; this enum only selects WHICH point freezes.
/// </summary>
public enum RotationPivot
{
    /// <summary>Normal in-place rotation (no orbit).</summary>
    Self,
    /// <summary>Orbit around the primary bone parent's frozen model-space position.</summary>
    Parent,
}
