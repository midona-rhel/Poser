using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Drives <see cref="ImDrawListPtr.ChannelsSplit"/> based layering for
/// <see cref="ElementStyle.ZIndex"/>. Default is 4 channels — background (0),
/// content (1, default), popover (2), tooltip (3) — selected by clamping the
/// element's ZIndex into the channel range.
///
/// <para>Only active inside a frame in which at least one root element
/// observed a non-zero ZIndex. Otherwise channels stay merged and the cost is zero.</para>
/// </summary>
public static class LayerManager
{
    public const int ChannelCount = 4;
    public const int DefaultChannel = 1;

    [System.ThreadStatic]
    private static int _frameId;
    [System.ThreadStatic]
    private static bool _splitOpen;

    /// <summary>
    /// Open a channel split for this frame if not already open. Idempotent per frame.
    /// Caller is responsible for matching <see cref="EndFrame"/>.
    /// </summary>
    public static void EnsureSplitForFrame(ImDrawListPtr drawList)
    {
        int frame = ImGui.GetFrameCount();
        if (_splitOpen && _frameId == frame) return;

        if (_splitOpen)
        {
            // Carry-over from a stale frame — merge defensively.
            drawList.ChannelsMerge();
            _splitOpen = false;
        }

        drawList.ChannelsSplit(ChannelCount);
        drawList.ChannelsSetCurrent(DefaultChannel);
        _splitOpen = true;
        _frameId = frame;
    }

    /// <summary>Map a user ZIndex to a draw channel. 0 → default content, &lt;0 → background, &gt;0 → popover/tooltip.</summary>
    public static int ChannelFor(int zIndex)
    {
        if (zIndex <= -1) return 0;
        if (zIndex >= 100) return ChannelCount - 1;
        if (zIndex >= 1) return ChannelCount - 2;
        return DefaultChannel;
    }

    /// <summary>Switch the draw list to the channel for the given z-index. Returns the prior channel.</summary>
    public static int Switch(ImDrawListPtr drawList, int zIndex)
    {
        EnsureSplitForFrame(drawList);
        int target = ChannelFor(zIndex);
        drawList.ChannelsSetCurrent(target);
        return DefaultChannel;
    }

    /// <summary>Restore a previously-saved channel.</summary>
    public static void Restore(ImDrawListPtr drawList, int previousChannel)
    {
        if (!_splitOpen) return;
        drawList.ChannelsSetCurrent(previousChannel);
    }

    /// <summary>
    /// Merge all open channels back together. Should be called once after the root
    /// element finishes rendering. Currently invoked lazily — if a frame ends with
    /// channels open, the next frame's <see cref="EnsureSplitForFrame"/> merges them.
    /// </summary>
    public static void EndFrame(ImDrawListPtr drawList)
    {
        if (!_splitOpen) return;
        drawList.ChannelsMerge();
        _splitOpen = false;
    }
}
