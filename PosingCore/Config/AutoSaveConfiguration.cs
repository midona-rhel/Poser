namespace Poser.Config;

/// <summary>
/// Timed pose auto-save (Ktisis <c>PoseAutoSave</c> / Brio <c>AutoSaveService</c>
/// parity). While GPose is active every actor carrying Poser-authored edits is
/// exported to <c>&lt;configDir&gt;/AutoSaves/&lt;timestamp&gt;/&lt;actor&gt;.pose</c>
/// on the interval, and once more when GPose is left.
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
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How many snapshot folders are retained on disk. Older folders are pruned
    /// after every save. Floored at 1 by the service.
    /// </summary>
    public int MaxAutoSaves { get; set; } = 10;

    /// <summary>
    /// Delete every snapshot when GPose is left instead of taking a final one
    /// (Brio <c>CleanAutoSaveOnLeavingGpose</c>). A crash never runs this, so
    /// snapshots still survive for recovery — only a clean exit clears them.
    /// </summary>
    public bool CleanOnExit { get; set; } = false;
}
