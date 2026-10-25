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
    /// Seconds between snapshots while in GPose. 60 matches both references'
    /// default. Entering GPose does not save immediately; the first snapshot
    /// lands one interval in.
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
    /// Where the per-actor snapshot folders are written. BLANK means the
    /// shipped <c>&lt;configDir&gt;/AutoSaves</c> — the composition root fills
    /// it in with that resolved path the first time it constructs the service,
    /// so the settings page always shows a real folder rather than an empty
    /// field that means something. Unlike the library homes this is read ONCE,
    /// in the service's constructor, so a change to it needs a plugin reload.
    /// </summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>
    /// Answers the effective root, seeding a blank one with the shipped path.
    /// Seeded in memory only, exactly as the library's shipped sources are: it
    /// persists with the next save the user causes rather than writing the
    /// configuration file on every startup.
    /// </summary>
    public string EnsureRoot(string shipped)
    {
        if (string.IsNullOrWhiteSpace(RootDirectory))
            RootDirectory = shipped;
        return RootDirectory;
    }
}
