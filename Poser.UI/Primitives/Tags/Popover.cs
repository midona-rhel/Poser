using System;
using System.Numerics;

namespace Poser.UI;

public record struct PopoverProps
{
    /// <summary>Unscaled width. The popover never sizes to content —
    /// a search result list would resize under the pointer as the user
    /// types, which moves the rows they are aiming at.</summary>
    public float Width;
    /// <summary>Unscaled height.</summary>
    public float Height;
    /// <summary>Screen rect the popover anchors under, in pixels. It
    /// flips above when there is no room below.</summary>
    public Vector2 AnchorMin;
    public Vector2 AnchorMax;
    /// <summary>Unscaled inner padding. Default 8.</summary>
    public float Padding;
}

public static partial class Crystarium
{
    /// <summary>
    /// Anchored glass popover with a caller-supplied body — the shared
    /// shell behind pickers and any other "click a control, get a panel"
    /// surface.
    ///
    /// It is the same glass recipe as ContextMenu, Modal and ColorWell
    /// (backdrop blur, the border trio, radius 8), which those three each
    /// duplicated inline; this is the one place it now lives. Unlike
    /// ContextMenu it is a fixed size and scrolls, and unlike Modal it
    /// does not block input behind it.
    ///
    /// Open it with <c>ImGui.OpenPopup(id)</c>. Returns true while it is
    /// open, after invoking <paramref name="body"/>.
    /// </summary>
    public static bool Popover(string id, in PopoverProps props, Action body)
        => FloatingSurface.Popup(
            id,
            new FloatingSurfaceProps
            {
                Width = props.Width,
                Height = props.Height,
                Padding = props.Padding > 0f
                    ? props.Padding
                    : Theme.Metrics.Floating.PopoverPadding,
                AnchorMin = props.AnchorMin,
                AnchorMax = props.AnchorMax,
            },
            body);
}
