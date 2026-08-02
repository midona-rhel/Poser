namespace Poser.UI;

/// <summary>
/// The text half of a sheet. Typography INHERITS, so a sheet that says
/// nothing leaves a run on whatever its ancestors resolved.
/// </summary>
public readonly record struct TypographySheet
{
    public float? FontSize { get; init; }

    public FontFamily? Font { get; init; }

    public FontWeight? Weight { get; init; }

    /// <summary>What a run does when its arranged box is narrower than it.
    /// EXPLICIT: sizing says how much room a run occupies, not that it may
    /// not spill.</summary>
    public TextOverflow? Overflow { get; init; }
}
