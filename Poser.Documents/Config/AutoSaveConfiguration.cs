namespace Poser.Documents.Config;

/// <summary>
/// Whole-scene autosave settings. Legacy pose settings remain readable for
/// compatibility, but the production timer writes only scene snapshots.
/// </summary>
public class AutoSaveConfiguration
{
    /// <summary>Whether the timed auto-save runs at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between snapshots while in GPose. Entering GPose does not save
    /// immediately; the first snapshot is scheduled after one full interval.
    /// </summary>
    public int IntervalSeconds { get; set; } = 180;

    /// <summary>
    /// How many snapshot folders are retained on disk. Older folders are pruned
    /// after every save. Floored at 1 by the service.
    /// </summary>
    public int MaxAutoSaves { get; set; } = 200;

    /// <summary>
    /// Delete every snapshot when GPose is left instead of taking a final one
    /// (Brio <c>CleanAutoSaveOnLeavingGpose</c>). A crash never runs this, so
    /// snapshots still survive for recovery — only a clean exit clears them.
    /// </summary>
    public bool CleanOnExit { get; set; } = false;

    /// <summary>
    /// Legacy setting retained for configuration compatibility. Whole-scene
    /// autosave now follows Enabled regardless of this value.
    /// </summary>
    public bool SceneSnapshots { get; set; } = true;

    /// <summary>How many whole-scene snapshots are retained on disk. Floored
    /// at 1 by the service.</summary>
    public int MaxSceneSnapshots { get; set; } = 5;

    /// <summary>
    /// Folder for legacy per-actor pose snapshots. An empty value selects
    /// <c>&lt;configDir&gt;/AutoSaves</c>. The service reads this setting during
    /// construction, so changing it requires a plugin reload.
    /// </summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>
    /// Returns the configured folder. If it is empty, stores the default in
    /// memory; normal configuration persistence writes it later.
    /// </summary>
    public string EnsureRoot(string shipped)
    {
        if (string.IsNullOrWhiteSpace(RootDirectory))
            RootDirectory = shipped;
        return RootDirectory;
    }
}
