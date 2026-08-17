using Poser.UI;

namespace Poser.ContractTests;

public sealed class PoseImportLayoutContractTests
{
    [Fact]
    public void Rejected_capture_uses_a_right_rail_and_three_group_columns()
    {
        var dialog = PoseImportDialogLayout.Create(
            width: 912f,
            contentTop: 94f,
            footerTop: 681f,
            railWidth: 236f,
            ruleWidth: 1f,
            inset: 12f,
            rowHeight: 24f,
            headerHeight: 20f,
            statusHeight: 16f);
        var grid = dialog.Options;

        Assert.Equal(3, PoseImportOptionsGrid.ColumnCount);
        Assert.Equal(6, PoseImportOptionsGrid.RequiredRows);
        Assert.Equal(676f, dialog.RailLeft);
        Assert.Equal(587f, dialog.RailHeight);
        Assert.Equal((675f - 24f) / 3f, grid.ColumnWidth);
        Assert.Equal(12f, grid.ColumnX(0));
        Assert.Equal(12f + grid.ColumnWidth, grid.ColumnX(1));
        Assert.Equal(12f + grid.ColumnWidth * 2f, grid.ColumnX(2));
        Assert.True(grid.FirstControlY > grid.RowY(0));
        Assert.True(grid.TypeControlY > grid.TypeHeaderY);
        Assert.Equal(188f, grid.Height);
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
