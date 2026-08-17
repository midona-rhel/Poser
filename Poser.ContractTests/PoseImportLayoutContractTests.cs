using Poser.UI;

namespace Poser.ContractTests;

public sealed class PoseImportLayoutContractTests
{
    [Fact]
    public void Rejected_capture_uses_compact_columns_and_a_full_height_rail()
    {
        var dialog = PoseImportDialogLayout.Create(
            width: 912f,
            contentTop: 94f,
            footerTop: 681f,
            railWidth: 272f,
            ruleWidth: 1f,
            horizontalInset: 8f,
            verticalInset: 6f,
            columnGap: 6f,
            rowHeight: 24f,
            headerHeight: 20f,
            statusHeight: 16f);
        var grid = dialog.Options;

        Assert.Equal(3, PoseImportOptionsGrid.ColumnCount);
        Assert.Equal(640f, dialog.RailLeft);
        Assert.Equal(587f, dialog.RailHeight);
        Assert.Equal((639f - 16f - 12f) / 3f, grid.ColumnWidth);
        Assert.Equal(8f, grid.ColumnX(0));
        Assert.Equal(8f + grid.ColumnWidth + 6f, grid.ColumnX(1));
        Assert.Equal(8f + (grid.ColumnWidth + 6f) * 2f, grid.ColumnX(2));
        Assert.Equal(grid.ColumnX(1), grid.ApplyX);
        Assert.Equal(grid.ApplyX, grid.ScopeX);
        Assert.Equal(grid.ColumnWidth * 2f + 6f, grid.ScopeWidth);
        Assert.Equal(74f, grid.ScopeHeaderY);
        Assert.Equal(172f, grid.Height);
    }

    [Fact]
    public void Inspector_and_picker_share_the_same_second_checkbox_column()
    {
        var grid = PoseImportOptionsGrid.Create(
            width: 639f,
            horizontalInset: 8f,
            verticalInset: 6f,
            columnGap: 6f,
            rowHeight: 24f,
            headerHeight: 20f,
            statusHeight: 16f);

        Assert.Equal(96f, PoseImportOptionsGrid.CheckboxColumnPitch);
        Assert.Equal(
            grid.ApplyX + PoseImportOptionsGrid.CheckboxColumnPitch,
            grid.ApplySecondColumnX);
    }

    [Fact]
    public void Preview_uses_a_padded_portrait_width_and_camera_bottom_gap()
    {
        var layout = PoseImportPreviewLayout.Create(
            railWidth: 272f,
            horizontalInset: 8f,
            height: 587f,
            cameraHeight: 28f,
            cameraBottomPadding: 8f);

        Assert.Equal(272f, layout.RailWidth);
        Assert.Equal(8f, layout.HorizontalInset);
        Assert.Equal(256f, layout.ImageWidth);
        Assert.Equal(551f, layout.ImageHeight);
        Assert.Equal(28f, layout.CameraHeight);
        Assert.Equal(8f, layout.CameraBottomPadding);
    }
}
