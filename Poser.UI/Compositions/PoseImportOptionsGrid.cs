using System;
using System.Numerics;

namespace Poser.UI;

/// <summary>Shared geometry for the pose-import options band.</summary>
public readonly record struct PoseImportOptionsGrid(
    float Width,
    float Inset,
    float RowHeight,
    float ColumnWidth)
{
    public const int ColumnCount = 3;
    public const int RequiredRows = 6;

    public float Height => Inset * 2f + RowHeight * RequiredRows;

    public static PoseImportOptionsGrid Create(
        float width, float inset, float rowHeight)
    {
        float content = MathF.Max(0f, width - inset * 2f);
        return new PoseImportOptionsGrid(
            MathF.Max(0f, width),
            MathF.Max(0f, inset),
            MathF.Max(1f, rowHeight),
            content / ColumnCount);
    }

    public float ColumnX(int column) => Inset + ColumnWidth * column;

    public float RowY(int row) => Inset + RowHeight * row;
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
