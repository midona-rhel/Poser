using Poser.Files;

namespace Poser.Services;

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

/// <summary>
/// The pose library's live preview: the game's own inspect CharaView (index 1)
/// renders a hidden body into <c>RenderTargetManager.CharaViewTextures[1]</c>,
/// and selected pose files are applied to that body through the ordinary
/// import pipeline. Ported from Ktisis 0.4 <c>PreviewNode</c> — the init /
/// per-tick Update+Render / Release sequence and the camera calls are its
/// exact semantics.
/// </summary>
