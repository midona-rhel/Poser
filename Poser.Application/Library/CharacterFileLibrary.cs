using Poser.Application.Settings;
using Poser.Documents.Files;
using Poser.Documents.Library;
using Poser.Domain;

namespace Poser.Application.Library;

/// <summary>Adds a character file (MCDF) to the user's MCDF library folder.</summary>
public interface ICharacterFileLibrary
{
    /// <summary>
    /// Copies the file into the MCDF library folder unless the library already
    /// holds the same file, matched by checksum. Runs on a worker thread; the
    /// result says where the file is, or why it could not be added.
    /// </summary>
    Task<Outcome> AddAsync(string path, CancellationToken cancellation = default);
}

public sealed class CharacterFileLibrary(
    ConfigurationService config, IMcdfHashIndex index, IPoseLibraryService library)
    : ICharacterFileLibrary
{
    public Task<Outcome> AddAsync(string path, CancellationToken cancellation = default) =>
        Task.Run(() => Add(path, cancellation), cancellation);

    private Outcome Add(string path, CancellationToken cancellation)
    {
        string root = config.Config.Library.ResolveMcdfRoot();
        if (!LibraryConfiguration.TryEnsureDirectory(root, out var unavailable))
            return Outcome.Fail($"The MCDF library folder is unavailable: {unavailable}");

        string source = Path.GetFullPath(path);
        if (IsInside(root, source))
            return new Outcome(true, "The character file is already in your library.");

        if (FileDigest.TryHashFile(source) is not { } hash)
            return Outcome.Fail("The character file could not be read.");
        if (index.Find(hash, cancellation) is { } existing)
            return new Outcome(true, $"Your library already has this character file: {Path.GetFileName(existing)}.");

        string target = FreeName(root, Path.GetFileName(source));
        string temporary = Path.Combine(root, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Copy beside the target first, so the library never lists half a file.
            File.Copy(source, temporary);
            File.Move(temporary, target);
        }
        catch (Exception ex)
        {
            try { File.Delete(temporary); } catch { }
            return Outcome.Fail($"The character file could not be copied into your library: {ex.Message}");
        }
        library.RequestScan();
        return new Outcome(true, $"Added {Path.GetFileName(target)} to your library.");
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    /// <summary>The file name, or "name (2).mcdf" and so on when it is taken.</summary>
    private static string FreeName(string root, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(root, fileName);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(root, $"{stem} ({n}){extension}");
        return candidate;
    }
}
