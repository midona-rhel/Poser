using System;
using System.Threading;
using Poser.Files;
using Poser.Library;

namespace Poser.Game.Scene;

/// <summary>Where a reference entry's package was found, or why it was not.
/// </summary>
internal enum SceneAppearanceOrigin
{
    /// <summary>A library package whose bytes match the recorded checksum.
    /// </summary>
    Library,

    /// <summary>The path the scene recorded, which the library could not
    /// better.</summary>
    RecordedPath,

    /// <summary>Neither answered.</summary>
    None,
}

/// <summary>One resolution answer: where to import from, and what to say about
/// it. <paramref name="Path"/> is null exactly when the origin is
/// <see cref="SceneAppearanceOrigin.None"/>, and <paramref name="Detail"/> then
/// carries the refusal rather than a note.</summary>
internal readonly record struct SceneAppearanceResolution(
    SceneAppearanceOrigin Origin,
    string? Path,
    string? Detail);

/// <summary>
/// WHICH file a reference appearance entry should be imported from — the
/// policy half of the restore, kept apart from the transaction half so the
/// order can be stated and tested without a live client.
///
/// <para>Content before location. The scene records the package's SHA-256, so
/// the library is searched for those exact bytes first: a package that was
/// renamed, filed into a subfolder or downloaded again elsewhere is still the
/// package the scene was saved against, and the recorded path is only a guess
/// that nothing ever moved. The path is the fallback, and when neither answers
/// the refusal states BOTH things that were tried.</para>
///
/// <para>An embedded portable payload never reaches here: its bytes ARE the
/// package, so there is nothing to resolve and nothing to identify them
/// against.</para>
/// </summary>
internal static class SceneAppearanceSource
{
    public static SceneAppearanceResolution Resolve(
        SceneActorMcdf saved,
        IMcdfHashIndex library,
        Func<string, bool> fileExists,
        CancellationToken cancellation = default)
    {
        bool hashed = saved.ContentHash.Length > 0;
        string? match = hashed
            ? library.Find(saved.ContentHash, cancellation)
            : null;

        if (match != null)
        {
            // Said out loud when it is not where the scene recorded it: the
            // actor gets the right appearance from somewhere the user did not
            // name, and a silent substitution leaves them wondering which file
            // they are looking at.
            bool moved = !string.Equals(
                match, saved.Path, StringComparison.OrdinalIgnoreCase);
            return new SceneAppearanceResolution(
                SceneAppearanceOrigin.Library,
                match,
                moved
                    ? $"The character file '{saved.FileName}' was matched by " +
                        $"checksum in your MCDF library at {match}."
                    : null);
        }

        if (!string.IsNullOrWhiteSpace(saved.Path) && fileExists(saved.Path))
        {
            return new SceneAppearanceResolution(
                SceneAppearanceOrigin.RecordedPath, saved.Path, null);
        }

        return new SceneAppearanceResolution(
            SceneAppearanceOrigin.None,
            null,
            $"The character file '{saved.FileName}' could not be found: " +
            (hashed
                ? "no package in your MCDF library matches the scene's checksum"
                : "the scene recorded no checksum to search your MCDF library " +
                    "with") +
            (string.IsNullOrWhiteSpace(saved.Path)
                ? ", and the scene recorded no path either"
                : $", and the recorded path {saved.Path} no longer exists") +
            ". The actor was restored without it.");
    }
}
