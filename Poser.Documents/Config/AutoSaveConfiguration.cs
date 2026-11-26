namespace Poser.Documents.Config;

/// <summary>The settings for saving the whole scene by itself while in
/// GPose, and the folder where older Poser versions kept pose backups.</summary>
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
    /// Delete every snapshot when GPose is left instead of taking a final one
    /// (Brio <c>CleanAutoSaveOnLeavingGpose</c>). A crash never runs this, so
    /// snapshots still survive for recovery — only a clean exit clears them.
    /// </summary>
    public bool CleanOnExit { get; set; } = false;

    /// <summary>How many whole-scene snapshots are retained on disk. Floored
    /// at 1 by the service.</summary>
    public int MaxSceneSnapshots { get; set; } = 5;

    /// <summary>
    /// The folder where older Poser versions saved pose backups. Poser no
    /// longer writes backups or changes this value; the Library's Auto-saves
    /// tab still lists what is in the folder. An empty value means
    /// <c>&lt;configDir&gt;/AutoSaves</c>. It is read once when Poser starts.
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
