namespace Poser.UI;

/// <summary>
/// The box half of a sheet. Layout is deliberately absent from
/// <see cref="LookSheet"/>: a pseudo state cannot reflow, and that rule is
/// enforced by construction rather than by review.
/// </summary>
public readonly record struct LayoutSheet
{
    public UiFlow? Flow { get; init; }

    public EdgeInsets? Padding { get; init; }

    public EdgeInsets? Margin { get; init; }

    public float? Gap { get; init; }

    public UiDim? Width { get; init; }

    public UiDim? Height { get; init; }

    /// <summary>CSS <c>max-width</c>, logical. The CLAMP, not a size: a Fill
    /// box still takes what it is offered and then stops growing here.</summary>
    public float? MaxWidth { get; init; }

    /// <summary>Cross axis.</summary>
    public UiAlign? Align { get; init; }

    /// <summary>Main axis.</summary>
    public UiAlign? Justify { get; init; }
}
