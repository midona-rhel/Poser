#if DEBUG
using System;
using System.IO;

namespace Poser.Lifecycle;

/// <summary>Reload-only isolation seam for destructive scene-autosave tests.</summary>
internal static class DebugSceneAutoSaveRoot
{
    internal const string EnvironmentName = "POSER_DEBUG_SCENE_AUTOSAVE_ROOT";
    private const string DisposableSegment =
        "/artifacts/refactor-review-481/autosave-disposable/";

    internal static string? Current =>
        Environment.GetEnvironmentVariable(EnvironmentName, EnvironmentVariableTarget.Process);

    internal static bool TryValidate(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string marked = normalized.Replace('\\', '/').TrimEnd('/') + "/";
            int marker = marked.IndexOf(DisposableSegment, StringComparison.OrdinalIgnoreCase);
            return marker >= 0 && marked.Length > marker + DisposableSegment.Length;
        }
        catch (Exception) when (path is not null)
        {
            normalized = string.Empty;
            return false;
        }
    }

    internal static string Resolve(string defaultRoot) =>
        TryValidate(Current, out var isolated) ? isolated : Path.GetFullPath(defaultRoot);

    internal static void Set(string? path) =>
        Environment.SetEnvironmentVariable(
            EnvironmentName, path, EnvironmentVariableTarget.Process);
}
#endif
