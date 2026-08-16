using System.Numerics;
using Poser.UI;

namespace Poser.ContractTests;

public sealed class ContextMenuContractTests
{
    [Fact]
    public void Submenu_stays_aligned_and_flips_with_one_pixel_gap()
    {
        var right = Crystarium.FloatingMenu.PlaceSubmenu(
            new Vector2(100f, 80f),
            new Vector2(240f, 300f),
            new Vector2(100f, 160f),
            new Vector2(180f, 120f),
            new Vector2(800f, 600f),
            2f,
            8f);

        Assert.Equal(new Vector2(341f, 144f), right);

        var left = Crystarium.FloatingMenu.PlaceSubmenu(
            new Vector2(600f, 80f),
            new Vector2(180f, 300f),
            new Vector2(600f, 160f),
            new Vector2(180f, 120f),
            new Vector2(800f, 600f),
            2f,
            8f);

        Assert.Equal(new Vector2(419f, 144f), left);
    }

    [Fact]
    public void Submenu_hover_dismissal_and_click_ownership_are_stateful()
    {
        var child = new[]
        {
            new ContextMenuItem("Import", TablerIcon.Download),
            new ContextMenuItem("Export", TablerIcon.DeviceFloppy, disabled: true),
        };
        var parent = new ContextMenuItem(
            "Pose files", TablerIcon.Folder, submenuItems: child);

        Assert.Same(child, parent.SubmenuItems);
        Assert.Equal("Import", parent.SubmenuItems![0].Label);
        Assert.True(parent.SubmenuItems[1].Disabled);

        var parentMin = new Vector2(100f, 80f);
        var parentSize = new Vector2(240f, 300f);
        var parentRowMin = new Vector2(100f, 160f);
        var parentRowMax = new Vector2(340f, 192f);
        var submenuMin = new Vector2(341f, 152f);
        var submenuSize = new Vector2(180f, 120f);

        // The live row logic keeps the child open while the pointer crosses
        // the one-pixel bridge between the two surfaces.
        Assert.True(Crystarium.FloatingMenu.KeepSubmenuOpen(
            new Vector2(340.5f, 176f), parentRowMin, parentRowMax,
            submenuMin, submenuSize, parentMin, parentSize));
        Assert.True(Crystarium.FloatingMenu.KeepSubmenuOpen(
            new Vector2(350f, 176f), parentRowMin, parentRowMax,
            submenuMin, submenuSize, parentMin, parentSize));

        Assert.True(Crystarium.FloatingMenu.IsMenuOrSubmenuPointerWithin(
            new Vector2(350f, 176f), parentMin, parentSize,
            parent.SubmenuItems, submenuMin, submenuSize));
        Assert.False(Crystarium.FloatingMenu.IsMenuOrSubmenuPointerWithin(
            new Vector2(20f, 20f), parentMin, parentSize,
            parent.SubmenuItems, submenuMin, submenuSize));
        Assert.True(Crystarium.FloatingMenu.ShouldDismiss(
            outsidePressed: true, pointerWithinMenu: false, escapePressed: false));
        Assert.True(Crystarium.FloatingMenu.ShouldDismiss(
            outsidePressed: false, pointerWithinMenu: true, escapePressed: true));

        int pending = 0;
        Assert.Equal(0, Crystarium.FloatingMenu.ConsumeSubmenuClick(
            ref pending, parent.SubmenuItems));
        Assert.Equal(-1, Crystarium.FloatingMenu.ConsumeSubmenuClick(
            ref pending, parent.SubmenuItems));
        Assert.Equal(-1, Crystarium.FloatingMenu.AcceptSubmenuClick(
            1, parent.SubmenuItems));
    }
}
