namespace Poser.Documents.Files;

/// <summary>
/// Identifies the pose shown by the preview actor. The pose may come from a
/// file or from an in-memory baseline with no file path.
/// </summary>
/// <param name="Key">Stable value used to detect an unchanged request: the
/// path for a file, or a caller-provided ID for an in-memory pose.</param>
public readonly record struct PosePreviewRequest(
    string Key, string? Path, PoseFile? Pose, PoseImportOptions Options)
{
    public static PosePreviewRequest File(
        string path, PoseImportOptions options) =>
        new(path, path, null, options);

    public static PosePreviewRequest Memory(
        PoseFile pose, string key, PoseImportOptions options) =>
        new(key, null, pose, options);
}
