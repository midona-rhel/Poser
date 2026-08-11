namespace Poser.Config;

/// <summary>
/// Configuration for skeleton overlay display settings.
/// </summary>
public class SkeletonConfiguration
{
    // Sizes
    public float BoneDotRadius { get; set; } = 3.4f;
    public float BoneLineThickness { get; set; } = 1.0f;
    public float BoneLineOpacity { get; set; } = 0.232f;
    public float BoneLineOpacityWhileUsing { get; set; } = 0.150f;
    public float OctahedraWidth { get; set; } = 4f;

    // Colors (uint ABGR format like Brio).
    // Selected/hovered default to the baked baseline accent (dark-theme
    // primary #3297FF) — this layer cannot see the theme. While the stored
    // value still equals its Default* constant, SkeletonOverlayWindow
    // substitutes the LIVE accent (theme + AccentIndex); an explicit
    // ColorWell override pins the stored value. Everything else is a
    // deliberate muted tone so unselected states recede over game scenery.
    public const uint DefaultSelectedBoneColor = 0xFFFF9732; // Accent #3297FF
    public const uint DefaultHoveredBoneColor = 0xFFFFBB7A;  // Accent +35% white #7ABBFF

    public uint BoneColor { get; set; } = 0xFFB8A394;        // Slate #94A3B8 — inactive dots + lines
    public uint BoneOutlineColor { get; set; } = 0xFF000000; // Black
    public uint SelectedBoneColor { get; set; } = DefaultSelectedBoneColor;
    public uint ModifiedBoneColor { get; set; } = 0xFF7CB563; // Jade #63B57C
    public uint HoveredBoneColor { get; set; } = DefaultHoveredBoneColor;
    public uint IkChainColor { get; set; } = 0xFF44A5D9;      // Amber #D9A544
    public uint MirroredBoneColor { get; set; } = 0xFFA07BC2; // Rose #C27BA0

    // Display options
    public bool ShowSkeletonLines { get; set; } = true;
}
