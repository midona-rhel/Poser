using System;

namespace Poser.UI;

/// <summary>Shared geometry for the pose-import options band.</summary>
public readonly record struct PoseImportOptionsGrid(
    float Width,
    float HorizontalInset,
    float VerticalInset,
    float ColumnGap,
    float RowHeight,
    float HeaderHeight,
    float StatusHeight,
    int StatusRows,
    float ColumnWidth)
{
    public const int ColumnCount = 3;
    public const float CheckboxColumnPitch = 96f;

    public float Height => VerticalInset * 2f + MathF.Max(
        HeaderHeight + RowHeight * 4f,
        HeaderHeight * 2f + RowHeight * 3f + StatusHeight * StatusRows);

    public static PoseImportOptionsGrid Create(
        float width,
        float horizontalInset,
        float verticalInset,
        float columnGap,
        float rowHeight,
        float headerHeight,
        float statusHeight,
        int statusRows)
    {
        float gap = MathF.Max(0f, columnGap);
        float content = MathF.Max(0f,
            width - horizontalInset * 2f - gap * (ColumnCount - 1));
        return new PoseImportOptionsGrid(
            MathF.Max(0f, width),
            MathF.Max(0f, horizontalInset),
            MathF.Max(0f, verticalInset),
            gap,
            MathF.Max(1f, rowHeight),
            MathF.Max(1f, headerHeight),
            MathF.Max(1f, statusHeight),
            Math.Max(0, statusRows),
            content / ColumnCount);
    }

    public float ColumnX(int column) => HorizontalInset
        + (ColumnWidth + ColumnGap) * column;

    public float RowY(int row) => VerticalInset + RowHeight * row;

    public float FirstControlY => VerticalInset + HeaderHeight;

    public float TypeHeaderY => FirstControlY + RowHeight * 2f;

    public float TypeControlY => TypeHeaderY + HeaderHeight;

    public float OptionsX => ColumnX(0);

    public float OptionsContinuationX => ColumnX(1);

    public float ApplyX => ColumnX(2);
}
