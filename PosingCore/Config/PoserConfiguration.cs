using Dalamud.Configuration;
using Poser.Library;

namespace Poser.Config;

/// <summary>
/// Main configuration for Poser plugin.
/// Implements IPluginConfiguration for Dalamud persistence.
/// </summary>
public class PoserConfiguration : IPluginConfiguration
{
    // 2: overlay color redesign — stored overlay colors reset once on load
    // (ConfigurationService.MigrateConfig); sizes/opacity keep user values.
    public int Version { get; set; } = 2;

    public SkeletonConfiguration Skeleton { get; set; } = new();
    public DisplayConfiguration Display { get; set; } = new();
    public UIConfiguration UI { get; set; } = new();
    public IntegrationConfiguration Integration { get; set; } = new();
    public LibraryConfiguration Library { get; set; } = new();

    // Behavior (Settings -> General)
    public bool OpenOnGPoseEnter { get; set; } = true;
    public bool CloseWithGPose { get; set; } = false;

    // Target sync (Brio parity): selection drives the GPose target and the
    // GPose target drives selection; both on by default like Brio.
    public bool SelectionChangesGPoseTarget { get; set; } = true;
    public bool GPoseTargetChangesSelection { get; set; } = true;

    /// <summary>
    /// Park the authored pose while a slot skeleton is rebuilt and restore it
    /// onto the replacement (Ktisis semantics: rotation everywhere, position on
    /// the pose root only). Off means a redraw keeps the current behaviour of
    /// giving the replacement a fresh, empty pose store.
    /// </summary>
    public bool PreservePoseAcrossRedraws { get; set; } = true;
}
