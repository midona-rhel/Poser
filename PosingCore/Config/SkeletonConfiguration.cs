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

    // Colors (uint ABGR format like Brio)
    public uint BoneColor { get; set; } = 0xFFFF9F68;        // Orange-ish (Ktisis default)
    public uint BoneOutlineColor { get; set; } = 0xFF000000; // Black
    public uint SelectedBoneColor { get; set; } = 0xFF00D9FF; // Cyan/yellow
    public uint ModifiedBoneColor { get; set; } = 0xFF00FF7F; // Green
    public uint HoveredBoneColor { get; set; } = 0xFFFF0073;  // Pink
    public uint IkChainColor { get; set; } = 0xFF0A9FFF;      // Orange #FF9F0A (ABGR)
    public uint MirroredBoneColor { get; set; } = 0xFFA0D37E; // Mint #7ED3A0 (ABGR)

    // Display options
    public bool ShowSkeletonLines { get; set; } = true;
}
