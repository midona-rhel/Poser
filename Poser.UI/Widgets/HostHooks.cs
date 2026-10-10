using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Utility;

namespace Poser.UI.Widgets;

public static class HostHooks
{
    /// <summary>Host hook for uploading a baked panel-shadow RGBA8 asset.</summary>
    public static Func<byte[], int, int, (nint Handle, IDisposable? Keepalive)>?
        PanelShadowTextureUploader
    {
        get => BoxShadowTextureCache.Uploader;
        set => BoxShadowTextureCache.Uploader = value;
    }

    public static Func<byte[], int, int, (nint Handle, IDisposable? Keepalive)>?
        IconTextureUploader
    {
        get => SvgIconTextureCache.Uploader;
        set => SvgIconTextureCache.Uploader = value;
    }

    /// <summary>Whether bounded startup icon warming has finished.</summary>
    public static bool StartupIconsReady => SvgIconTextureCache.StartupIconsReady;

    /// <summary>Advances bounded startup icon warming on the UI thread.</summary>
    public static void PumpStartupIcons(float libraryIconSize) =>
        SvgIconTextureCache.PumpStartupIcons(libraryIconSize);

    /// <summary>Diagnostics sink — the host wires it to its debug log.
    /// Poser.UI stays free of Dalamud, so the seam is one delegate.</summary>
    public static Action<string>? Log;
}
