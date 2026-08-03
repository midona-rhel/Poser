using System;

namespace Poser.Services;

/// <summary>
/// Timed pose auto-save. While GPose is active and
/// <c>PoserConfiguration.AutoSave.Enabled</c> is set, every actor carrying
/// Poser-authored (unnamed-layer) edits is exported through
/// <see cref="IPoseFileService.ExportPose"/> into a timestamped folder under
/// <see cref="RootDirectory"/> on the configured interval, plus once when GPose
/// is left. Files are byte-identical to a manual export; only the location and
/// the trigger differ.
///
/// Snapshot folders older than <c>AutoSave.MaxAutoSaves</c> are pruned from disk
/// after each save, so retention survives a plugin restart.
/// </summary>
public interface IAutoSaveService : IDisposable
{
    /// <summary>
    /// Directory holding the timestamped snapshot folders
    /// (<c>&lt;pluginConfigDir&gt;/AutoSaves</c>). Stable for the service's
    /// lifetime; it may not exist if creating it failed.
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// UTC time of the last snapshot that actually wrote a folder, or null when
    /// none has been taken this session. A skipped save (no actor had authored
    /// edits) does not update this.
    /// </summary>
    DateTime? LastSaveUtc { get; }

    /// <summary>
    /// Takes a snapshot immediately, regardless of the interval, and returns the
    /// number of actors successfully written (0 when nothing had authored edits,
    /// in which case no folder is created). Never throws: every failure is
    /// logged and the remaining actors are still attempted.
    /// </summary>
    /// <param name="reason">Short tag recorded in the log line, e.g. "interval".</param>
    int SaveNow(string reason);
}
