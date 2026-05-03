using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Chrome-only style: background, border, shadow, gradient. Used by Crystarium.Box
/// (low-level, draws at an explicit screen rect with no cursor manipulation) and
/// internally by Element when rendering its own chrome.
/// </summary>
public record struct BoxStyle
{
    public Vector4? BackgroundColor;
    public Gradient? BackgroundGradient;
    public Vector4? BorderColor;
    public float BorderWidth;
    public float BorderRadius;
    public BoxShadow? BoxShadow;
    public BoxShadow[]? BoxShadows;
    public Outline? Outline;

    /// <summary>
    /// If true, paints a top-highlight + bottom-shadow gradient inside the box
    /// (matches the legacy "raised" PoserButton look). Skipped when caller wants flat.
    /// </summary>
    public bool RaisedGradient;
}
