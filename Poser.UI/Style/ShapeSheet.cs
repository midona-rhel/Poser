namespace Poser.UI;

/// <summary>The border-box geometry a state IS allowed to change: both values
/// are logical, and the paint pass owns the one conversion to pixels.</summary>
public readonly record struct ShapeSheet
{
    public float? Radius { get; init; }

    public float? BorderWidth { get; init; }
}
