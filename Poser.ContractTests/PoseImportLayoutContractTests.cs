using Poser.UI;

namespace Poser.ContractTests;

public sealed class PoseImportLayoutContractTests
{
    [Fact]
    public void Rejected_capture_uses_one_even_three_column_grid()
    {
        var grid = PoseImportOptionsGrid.Create(
            width: 928f, inset: 12f, rowHeight: 24f);

        Assert.Equal(3, PoseImportOptionsGrid.ColumnCount);
        Assert.Equal(6, PoseImportOptionsGrid.RequiredRows);
        Assert.Equal((928f - 24f) / 3f, grid.ColumnWidth);
        Assert.Equal(12f, grid.ColumnX(0));
        Assert.Equal(12f + grid.ColumnWidth, grid.ColumnX(1));
        Assert.Equal(12f + grid.ColumnWidth * 2f, grid.ColumnX(2));
        Assert.Equal(12f, grid.RowY(0));
        Assert.Equal(12f + 24f, grid.RowY(1));
        Assert.Equal(168f, grid.Height);
    }

    [Fact]
    public void Preview_fills_the_column_above_the_camera_row()
    {
        var layout = PoseImportPreviewLayout.Create(
            width: 236f, height: 408f, cameraHeight: 28f);

        Assert.Equal(236f, layout.Width);
        Assert.Equal(380f, layout.ImageHeight);
        Assert.Equal(28f, layout.CameraHeight);
    }
}
