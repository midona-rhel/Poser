using Dalamud.Interface.Windowing;
using Poser.Config;
using Poser.Services;
using System;

namespace Poser.UI.Composition;

/// <summary>
/// Owns draw order for the focused posing workspace, settings, the spawn
/// browser, and the two viewport interaction canvases.
/// </summary>
public sealed class UiWindowSet : IDisposable
{
    public WindowSystem System { get; } = new(PluginConstants.PluginName);
    public MainWindow Main { get; }
    public GizmoOverlayWindow GizmoOverlay { get; }
    public SkeletonOverlayWindow SkeletonOverlay { get; }
    public SettingsWindow Settings { get; }
    public SpawnBrowserWindow SpawnBrowser { get; }
    private readonly SkeletonOverlayPresentation _overlayPresentation;

    public UiWindowSet(
        IGPoseService gPoseService,
        ConfigurationService configService,
        MainWindow main,
        SkeletonOverlayWindow skeletonOverlay,
        GizmoOverlayWindow gizmoOverlay,
        SettingsWindow settings,
        SpawnBrowserWindow spawnBrowser,
        SkeletonOverlayPresentation overlayPresentation)
    {
        _overlayPresentation = overlayPresentation;
        // Draw order is intentional: overlays first, normal windows after them.
        SkeletonOverlay = skeletonOverlay;
        System.AddWindow(SkeletonOverlay);

        GizmoOverlay = gizmoOverlay;
        System.AddWindow(GizmoOverlay);

        Main = main;
        System.AddWindow(Main);

        Settings = settings;
        System.AddWindow(Settings);

        SpawnBrowser = spawnBrowser;
        System.AddWindow(SpawnBrowser);

        Main.GetSkeletonOverlayOn = () => SkeletonOverlay.UserVisible;
        Main.OnSkeletonOverlayToggled += SetSkeletonOverlayOpen;

        // Loading mid-GPose obeys the same rule as entering it: the workspace
        // only appears when the user asked for it to.
        SetPrimaryOpen(
            gPoseService.IsGPosing && configService.Config.OpenOnGPoseEnter);
    }

    public void SetPrimaryOpen(bool isOpen)
    {
        Main.IsOpen = isOpen;
        GizmoOverlay.IsOpen = isOpen;
        // The window itself follows the session like the gizmo overlay; the
        // Armature toggle starts Off each GPose/UI session and a bone
        // selection forces the armature visible regardless of the toggle.
        // Session end resets the toggle so the next session starts Off.
        SkeletonOverlay.IsOpen = isOpen;
        if (!isOpen)
        {
            SkeletonOverlay.UserVisible = false;
            _overlayPresentation.Clear();
        }
    }

    /// <summary>Every surface down, settings included. Only the
    /// Close-with-GPose path wants this; manual toggles do not.</summary>
    public void CloseAll()
    {
        SetPrimaryOpen(false);
        Settings.IsOpen = false;
    }

    private void SetSkeletonOverlayOpen(bool isOpen)
        => SkeletonOverlay.UserVisible = isOpen;

    public void Dispose()
    {
        Main.OnSkeletonOverlayToggled -= SetSkeletonOverlayOpen;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
