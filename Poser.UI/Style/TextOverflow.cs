namespace Poser.UI;

/// <summary>
/// What a text run does when its arranged box is narrower than the run. An
/// EXPLICIT property, not a consequence of sizing: giving a label a Fixed or
/// Fill width says how much space it occupies, which in CSS is
/// <c>overflow: visible</c> until something says otherwise. A control whose
/// label must be cut says so.
/// </summary>
public enum TextOverflow : byte
{
    /// <summary>The run renders at its INTRINSIC width even where the box is
    /// smaller. Anything that must not spill relies on an ancestor's clip —
    /// which is exactly how a button keeps its caption inside its border.
    /// </summary>
    Visible,

    /// <summary>The run is cut to the arranged box, with the renderer's own
    /// ellipsis treatment — applied ONLY when the run overflows, because the
    /// clip's snapped line-height edge can shave a fitting run's descender
    /// (the accepted parity lesson, and a user-caught regression).</summary>
    Truncate,

    /// <summary>As <see cref="Truncate"/>, but the clip applies to a FITTING
    /// run too — the legacy renderer's behavior, shave included. Only for
    /// twins whose pixels are byte-frozen against a legacy control.</summary>
    Clip,
}
