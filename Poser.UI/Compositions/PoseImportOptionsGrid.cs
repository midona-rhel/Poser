using System;
using System.Numerics;

namespace Poser.UI;

/// <summary>Shared geometry for the pose-import options band.</summary>
public readonly record struct PoseImportOptionsGrid(
    float Width,
    float Inset,
    float RowHeight,
    float HeaderHeight,
    float StatusHeight,
    float ColumnWidth)
{
    public const int ColumnCount = 3;
    public const int RequiredRows = 6;

    public float Height => Inset * 2f + MathF.Max(
        HeaderHeight * 2f + RowHeight * 3f + StatusHeight * 2f,
        MathF.Max(
            HeaderHeight + RowHeight * 2f,
            HeaderHeight + RowHeight * RequiredRows));

    public static PoseImportOptionsGrid Create(
        float width,
        float inset,
        float rowHeight,
        float headerHeight,
        float statusHeight)
    {
        float content = MathF.Max(0f, width - inset * 2f);
        return new PoseImportOptionsGrid(
            MathF.Max(0f, width),
            MathF.Max(0f, inset),
            MathF.Max(1f, rowHeight),
            MathF.Max(1f, headerHeight),
            MathF.Max(1f, statusHeight),
            content / ColumnCount);
    }

    public float ColumnX(int column) => Inset + ColumnWidth * column;

    public float RowY(int row) => Inset + RowHeight * row;

    public float FirstControlY => Inset + HeaderHeight;

    public float TypeHeaderY => FirstControlY + RowHeight * 2f;

    public float TypeControlY => TypeHeaderY + HeaderHeight;
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
        float inset,
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
                mainWidth, inset, rowHeight, headerHeight, statusHeight));
    }
}

/// <summary>Geometry for the dialog preview and its camera controls.</summary>
public readonly record struct PoseImportPreviewLayout(
    float Width, float ImageHeight, float CameraHeight)
{
    public static PoseImportPreviewLayout Create(
        float width, float height, float cameraHeight) => new(
        MathF.Max(0f, width),
        MathF.Max(0f, height - cameraHeight),
        MathF.Max(0f, cameraHeight));
}
