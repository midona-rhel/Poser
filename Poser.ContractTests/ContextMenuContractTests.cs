extern alias ProductionPoser;

using System.Numerics;
using Poser.UI;
using MainWindow = ProductionPoser::Poser.UI.MainWindow;

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
        var rightHost = Crystarium.FloatingMenu.HostBounds(
            new Vector2(100f, 80f), new Vector2(240f, 300f), true,
            right, new Vector2(180f, 120f), 8f);
        Assert.Equal(new Vector2(92f, 72f), rightHost.Min);
        Assert.Equal(new Vector2(437f, 316f), rightHost.Size);

        var left = Crystarium.FloatingMenu.PlaceSubmenu(
            new Vector2(600f, 80f),
            new Vector2(180f, 300f),
            new Vector2(600f, 160f),
            new Vector2(180f, 120f),
            new Vector2(800f, 600f),
            2f,
            8f);

        Assert.Equal(new Vector2(419f, 144f), left);
        var leftHost = Crystarium.FloatingMenu.HostBounds(
            new Vector2(600f, 80f), new Vector2(180f, 300f), true,
            left, new Vector2(180f, 120f), 8f);
        Assert.Equal(new Vector2(411f, 72f), leftHost.Min);
        Assert.Equal(new Vector2(377f, 316f), leftHost.Size);

        var parentOnlyHost = Crystarium.FloatingMenu.HostBounds(
            new Vector2(100f, 80f), new Vector2(240f, 300f), false,
            default, default, 8f);
        Assert.Equal(new Vector2(92f, 72f), parentOnlyHost.Min);
        Assert.Equal(new Vector2(256f, 316f), parentOnlyHost.Size);
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

        // Keeps the submenu open while the pointer crosses the gap.
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

    [Fact]
    public void Production_menu_rows_use_shared_tabler_icons()
    {
        var shell = new ContextMenuItem[
            (int)MainWindow.ShellCommand.OpenSettings + 1];
        MainWindow.FillShellMenuItems(
            shell,
            poseTarget: true,
            detachedShell: true,
            sceneOpen: true,
            contentHidden: false);

        Assert.Equal("Show library", shell[(int)MainWindow.ShellCommand.ShowLibrary].Label);
        Assert.Equal(TablerIcon.Book, shell[(int)MainWindow.ShellCommand.ShowLibrary].Icon);
        Assert.Equal("Import pose", shell[(int)MainWindow.ShellCommand.ImportPose].Label);
        Assert.Equal(TablerIcon.Download, shell[(int)MainWindow.ShellCommand.ImportPose].Icon);
        Assert.Equal("Export pose", shell[(int)MainWindow.ShellCommand.ExportPose].Label);
        Assert.Equal(TablerIcon.Upload, shell[(int)MainWindow.ShellCommand.ExportPose].Icon);
        Assert.Equal("Pop out content", shell[(int)MainWindow.ShellCommand.PopOutContent].Label);
        Assert.Equal(TablerIcon.WindowMaximize, shell[(int)MainWindow.ShellCommand.PopOutContent].Icon);
        Assert.Equal("Close Scene window", shell[(int)MainWindow.ShellCommand.SceneWindow].Label);
        Assert.Equal(TablerIcon.DeviceIpadX, shell[(int)MainWindow.ShellCommand.SceneWindow].Icon);
        Assert.Equal("Close Inspector window", shell[(int)MainWindow.ShellCommand.InspectorWindow].Label);
        Assert.Equal(TablerIcon.BrowserX, shell[(int)MainWindow.ShellCommand.InspectorWindow].Icon);
        Assert.Equal("Merge the UI", shell[(int)MainWindow.ShellCommand.ToggleDetached].Label);
        Assert.Equal(TablerIcon.WindowMinimize, shell[(int)MainWindow.ShellCommand.ToggleDetached].Icon);

        MainWindow.FillShellMenuItems(
            shell,
            poseTarget: true,
            detachedShell: false,
            sceneOpen: false,
            contentHidden: true);
        Assert.Equal("Detach the UI", shell[(int)MainWindow.ShellCommand.ToggleDetached].Label);
        Assert.Equal(TablerIcon.WindowMaximize, shell[(int)MainWindow.ShellCommand.ToggleDetached].Icon);

        var actorRows = new List<ContextMenuItem>();
        MainWindow.AddActorPoseFileMenuItems(
            actorRows,
            hasSkeleton: true,
            hasStash: true,
            stashedFrom: "actor",
            stashedAt: DateTimeOffset.UnixEpoch);
        Assert.Equal("Import pose", actorRows[1].Label);
        Assert.Equal(TablerIcon.Download, actorRows[1].Icon);
        Assert.Equal("Export pose", actorRows[2].Label);
        Assert.Equal(TablerIcon.Upload, actorRows[2].Icon);
        Assert.Equal("Stash pose", actorRows[3].Label);
        Assert.Equal(TablerIcon.Stack2, actorRows[3].Icon);
        Assert.Equal("Apply stashed pose", actorRows[4].Label);
        Assert.Equal(TablerIcon.ArrowBackUp, actorRows[4].Icon);

        foreach (var icon in new[]
        {
            TablerIcon.Book,
            TablerIcon.Download,
            TablerIcon.Upload,
            TablerIcon.WindowMaximize,
            TablerIcon.WindowMinimize,
            TablerIcon.BrowserX,
            TablerIcon.DeviceIpadX,
            TablerIcon.Copy,
            TablerIcon.UserMinus,
            TablerIcon.Stack2,
            TablerIcon.ArrowBackUp,
            TablerIcon.X,
            TablerIcon.LayoutPanel,
            TablerIcon.LayoutSidebarLeft,
        })
        {
            Assert.NotNull(Tabler.Get(icon));
        }
    }
}
