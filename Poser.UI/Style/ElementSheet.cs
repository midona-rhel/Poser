namespace Poser.UI;

/// <summary>
/// One element's complete visual and layout spec. Every member is optional; a
/// null member is not part of the spec and resolution falls through inline
/// patch → active state look → family sheet → inherited context → renderer
/// default.
///
/// <para>Sheets are immutable and the runtime NEVER merges them: the walk
/// flattens the chain into one plain resolved value per element per frame.
/// Variants are whole sheets built with <c>with</c>-expressions when the theme
/// is constructed, which is why no variant enum survives inside a painter.
/// </para>
/// </summary>
public readonly record struct ElementSheet
{
    public ColorSheet? Colors { get; init; }

    public LayoutSheet? Layout { get; init; }

    public ShapeSheet? Shape { get; init; }

    public TypographySheet? Type { get; init; }

    public MotionSheet? Motion { get; init; }

    public LookSheet? Hover { get; init; }

    public LookSheet? Active { get; init; }

    public LookSheet? Disabled { get; init; }

    public LookSheet? Selected { get; init; }
}
