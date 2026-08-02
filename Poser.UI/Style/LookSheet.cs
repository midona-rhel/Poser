namespace Poser.UI;

/// <summary>
/// What a pseudo state may change: paint, and only paint. There is no layout
/// field here and that is the whole point — a hover cannot reflow, a
/// selection cannot resize, and neither rule needs enforcing because neither
/// is expressible.
/// </summary>
public readonly record struct LookSheet
{
    public ColorSheet? Colors { get; init; }

    public ShapeSheet? Shape { get; init; }

    public MotionSheet? Motion { get; init; }
}
