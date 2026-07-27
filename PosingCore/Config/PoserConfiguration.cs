using Dalamud.Configuration;

namespace Poser.Config;

/// <summary>
/// Main configuration for Poser plugin.
/// Implements IPluginConfiguration for Dalamud persistence.
/// </summary>
public class PoserConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public SkeletonConfiguration Skeleton { get; set; } = new();
    public DisplayConfiguration Display { get; set; } = new();
    public UIConfiguration UI { get; set; } = new();
    public IntegrationConfiguration Integration { get; set; } = new();

    // Behavior (Settings -> General)
    public bool OpenOnGPoseEnter { get; set; } = true;
    public bool CloseWithGPose { get; set; } = false;
}
