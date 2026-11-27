namespace Poser.Documents.Config;

/// <summary>The settings for saving the whole scene by itself while in
/// GPose, and the folder where older Poser versions kept pose backups.</summary>
public class AutoSaveConfiguration
{
    /// <summary>Whether the timed auto-save runs at all.</summary>
    public bool Enabled { get; set; } = true;

    private int _intervalSeconds = 180;
    private int _maxSceneSnapshots = 5;

    /// <summary>
    /// Seconds between snapshots while in GPose. Entering GPose does not save
    /// immediately; the first snapshot is scheduled after one full interval.
    /// The Settings slider offers 10 to 600 seconds; this floor of one second
    /// only stops an edited settings file from making Poser save the scene on
    /// every frame.
    /// </summary>
    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = Math.Max(1, value);
    }

    /// <summary>
    /// Delete every snapshot when GPose is left instead of taking a final one
    /// (Brio <c>CleanAutoSaveOnLeavingGpose</c>). A crash never runs this, so
    /// snapshots still survive for recovery — only a clean exit clears them.
    /// </summary>
    public bool CleanOnExit { get; set; } = false;

    /// <summary>How many whole-scene snapshots are kept on disk; older ones
    /// are deleted after each save. At least one, because the newest snapshot
    /// is the one that recovers the scene after a crash.</summary>
    public int MaxSceneSnapshots
    {
        get => _maxSceneSnapshots;
        set => _maxSceneSnapshots = Math.Max(1, value);
    }

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
