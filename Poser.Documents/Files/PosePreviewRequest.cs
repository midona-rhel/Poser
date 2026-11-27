namespace Poser.Documents.Files;

/// <summary>
/// The pose the preview actor should show. It is either a pose file that the
/// library or the import window has already read, or an in-memory starting
/// pose with no file.
/// </summary>
/// <param name="Key">Tells an unchanged request apart: the file's path, or a
/// name the caller picks for an in-memory pose.</param>
/// <param name="Path">The file's path; null for an in-memory pose.</param>
/// <param name="Pose">The pose to show; null when reading the file failed.</param>
/// <param name="ReadFailure">Why the file could not be read, shown over the
/// preview instead of a pose.</param>
public readonly record struct PosePreviewRequest(
    string Key, string? Path, PoseFile? Pose, PoseImportOptions Options, string? ReadFailure = null)
{
    public static PosePreviewRequest File(
        string path, PoseFileReadOutcome read, PoseImportOptions options) =>
        new(path, path, read.Pose, options, read.Failure?.Detail);

    public static PosePreviewRequest Memory(
        PoseFile pose, string key, PoseImportOptions options) =>
        new(key, null, pose, options);
}
