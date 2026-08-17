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
    float ColumnWidth)
{
    public const int ColumnCount = 3;
    public const float CheckboxColumnPitch = 96f;
    public const int ScopeRows = 3;

    public float Height => VerticalInset * 2f + MathF.Max(
        HeaderHeight * 2f + RowHeight * 3f + StatusHeight * 2f,
        HeaderHeight * 2f + RowHeight * (2f + ScopeRows));

    public static PoseImportOptionsGrid Create(
        float width,
        float horizontalInset,
        float verticalInset,
        float columnGap,
        float rowHeight,
        float headerHeight,
        float statusHeight)
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
            content / ColumnCount);
    }

    public float ColumnX(int column) => HorizontalInset
        + (ColumnWidth + ColumnGap) * column;

    public float RowY(int row) => VerticalInset + RowHeight * row;

    public float FirstControlY => VerticalInset + HeaderHeight;

    public float TypeHeaderY => FirstControlY + RowHeight * 2f;

    public float TypeControlY => TypeHeaderY + HeaderHeight;

    public float ApplyX => ColumnX(1);

    public float ApplySecondColumnX => ApplyX + CheckboxColumnPitch;

    public float ScopeX => ApplyX;

    public float ScopeWidth => ColumnWidth * 2f + ColumnGap;

    public float ScopeHeaderY => VerticalInset + HeaderHeight + RowHeight * 2f;
}

/// <summary>Geometry for the import dialog's main region and preview rail.</summary>
public readonly record struct PoseImportDialogLayout(
    float Width,
    float ContentTop,
    float FooterTop,
    float RailLeft,
    float RailWidth,
    PoseImportOptionsGrid Options)
{
    public float RailHeight => MathF.Max(0f, FooterTop - ContentTop);

    public static PoseImportDialogLayout Create(
        float width,
        float contentTop,
        float footerTop,
        float railWidth,
        float ruleWidth,
        float horizontalInset,
        float verticalInset,
        float columnGap,
        float rowHeight,
        float headerHeight,
        float statusHeight)
    {
        float mainWidth = MathF.Max(0f, width - railWidth - ruleWidth);
        return new PoseImportDialogLayout(
            MathF.Max(0f, width),
            contentTop,
            footerTop,
            mainWidth + ruleWidth,
            MathF.Max(0f, railWidth),
            PoseImportOptionsGrid.Create(
                mainWidth, horizontalInset, verticalInset, columnGap,
                rowHeight, headerHeight, statusHeight));
    }
}

/// <summary>Geometry for the dialog preview and its camera controls.</summary>
public readonly record struct PoseImportPreviewLayout(
    float RailWidth,
    float HorizontalInset,
    float ImageWidth,
    float ImageHeight,
    float CameraHeight,
    float CameraBottomPadding)
{
    public static PoseImportPreviewLayout Create(
        float railWidth,
        float horizontalInset,
        float height,
        float cameraHeight,
        float cameraBottomPadding)
    {
        float inset = MathF.Max(0f, horizontalInset);
        float width = MathF.Max(0f, railWidth - inset * 2f);
        float camera = MathF.Max(0f, cameraHeight);
        float bottom = MathF.Max(0f, cameraBottomPadding);
        return new PoseImportPreviewLayout(
            MathF.Max(0f, railWidth), inset, width,
            MathF.Max(0f, height - camera - bottom), camera, bottom);
    }
}
