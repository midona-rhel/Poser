namespace Poser.UI;

/// <summary>
/// Font weight axis, mirroring the picto token scale (400/500/600 — 600 is the maximum;
/// the design system uses no true bold). Values match CSS numeric weights.
/// </summary>
public enum FontWeight
{
    Regular = 400,
    Medium = 500,
    SemiBold = 600,
}
