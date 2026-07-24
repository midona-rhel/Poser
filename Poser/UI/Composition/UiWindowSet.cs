using Dalamud.Interface.Windowing;
using Poser.Services;
using System;

namespace Poser.UI.Composition;

/// <summary>
/// Owns draw order for the focused posing workspace, settings, and the two
/// viewport interaction canvases.
/// </summary>
public sealed class UiWindowSet : IDisposable
{
    public WindowSystem System { get; } = new(PluginConstants.PluginName);
    public MainWindow Main { get; }
    public GizmoOverlayWindow GizmoOverlay { get; }
    public SkeletonOverlayWindow SkeletonOverlay { get; }
    public SettingsWindow Settings { get; }

    public UiWindowSet(
        IGPoseService gPoseService,
        MainWindow main,
        SkeletonOverlayWindow skeletonOverlay,
        GizmoOverlayWindow gizmoOverlay,
        SettingsWindow settings)
    {
        // Draw order is intentional: overlays first, normal windows after them.
        SkeletonOverlay = skeletonOverlay;
        System.AddWindow(SkeletonOverlay);

        GizmoOverlay = gizmoOverlay;
        System.AddWindow(GizmoOverlay);

        Main = main;
        System.AddWindow(Main);

        Settings = settings;
        System.AddWindow(Settings);

        Main.GetSkeletonOverlayOn = () => SkeletonOverlay.IsOpen;
        Main.OnSkeletonOverlayToggled += SetSkeletonOverlayOpen;

        SetPrimaryOpen(gPoseService.IsGPosing);
    }

    public void SetPrimaryOpen(bool isOpen)
    {
        Main.IsOpen = isOpen;
        GizmoOverlay.IsOpen = isOpen;
        // The skeleton overlay starts Off each GPose/UI session: only the
        // toolbar Armature action opens it, and a user toggle persists for the
        // session. Session end still closes it so the next session starts Off.
        if (!isOpen)
            SkeletonOverlay.IsOpen = false;
    }

    private void SetSkeletonOverlayOpen(bool isOpen)
        => SkeletonOverlay.IsOpen = isOpen;

    public void Dispose()
    {
        Main.OnSkeletonOverlayToggled -= SetSkeletonOverlayOpen;
        System.RemoveAllWindows();
    }
}
