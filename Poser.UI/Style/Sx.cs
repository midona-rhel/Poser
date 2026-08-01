namespace Poser.UI;

/// <summary>
/// Style factories. A flow factory is a COMPLETE layout description, so it
/// marks every field as set — omitted arguments are deliberate defaults, not
/// "inherit". Partial overrides go through the single-field patch helpers
/// (<see cref="Pad"/>, <see cref="Gap"/>, <see cref="Size"/>,
/// <see cref="Justify"/>) fed to <see cref="Extend"/>.
/// </summary>
public static class Sx
{
    private const UiStyleFields AllFields =
        UiStyleFields.Flow | UiStyleFields.Gap | UiStyleFields.Padding | UiStyleFields.Margin |
        UiStyleFields.Width | UiStyleFields.Height | UiStyleFields.Justify | UiStyleFields.Align;

    public static UiStyle Row(
        float gap = 0,
        EdgeInsets padding = default,
        EdgeInsets margin = default,
        UiAlign justify = UiAlign.Start,
        UiAlign align = UiAlign.Start,
        UiDim width = default,
        UiDim height = default) =>
        new(AllFields, UiFlow.Row, gap, padding, margin, width, height, justify, align);

    public static UiStyle Column(
        float gap = 0,
        EdgeInsets padding = default,
        EdgeInsets margin = default,
        UiAlign justify = UiAlign.Start,
        UiAlign align = UiAlign.Start,
        UiDim width = default,
        UiDim height = default) =>
        new(AllFields, UiFlow.Column, gap, padding, margin, width, height, justify, align);

    public static UiStyle Stack(
        float gap = 0,
        EdgeInsets padding = default,
        EdgeInsets margin = default,
        UiAlign justify = UiAlign.Start,
        UiAlign align = UiAlign.Start,
        UiDim width = default,
        UiDim height = default) =>
        new(AllFields, UiFlow.Stack, gap, padding, margin, width, height, justify, align);

    public static UiStyle Pad(EdgeInsets padding) =>
        new(UiStyleFields.Padding, default, 0, padding, default, default, default, default, default);

    public static UiStyle Margin(EdgeInsets margin) =>
        new(UiStyleFields.Margin, default, 0, default, margin, default, default, default, default);

    public static UiStyle Gap(float gap) =>
        new(UiStyleFields.Gap, default, gap, default, default, default, default, default, default);

    public static UiStyle Size(UiDim w, UiDim h) =>
        new(UiStyleFields.Width | UiStyleFields.Height, default, 0, default, default, w, h, default, default);

    public static UiStyle Justify(UiAlign justify) =>
        new(UiStyleFields.Justify, default, 0, default, default, default, default, justify, default);

    public static UiStyle Align(UiAlign align) =>
        new(UiStyleFields.Align, default, 0, default, default, default, default, default, align);

    public static UiStyle Extend(in UiStyle b, in UiStyle patch) => UiStyle.Extend(b, patch);
}
