namespace Poser.Files;

/// <summary>
/// ONE pose the preview body should stand in: a file on disk, or a pose held
/// in memory (the rebase baseline, which no path names).
/// </summary>
/// <param name="Key">What the request is DEDUPED on in place of a path — the
/// path itself for a file, a caller-chosen stand-in for an in-memory pose.
/// Restating the same key with the same options INSTANCE is free.</param>
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
