namespace Poser.Core;

/// <summary>
/// Pivot selection for the explicit Orbit rotation mode. Every orbit routes
/// through the clean transform gesture with a pivot frozen at Begin; this
/// enum only selects WHICH point freezes.
/// </summary>
public enum OrbitPivotMode
{
    /// <summary>The primary bone's parent position (default).</summary>
    Parent,
    /// <summary>The frozen average position of the selection roots.</summary>
    SelectionCenter,
    /// <summary>A user-supplied world-space point.</summary>
    Custom,
}
