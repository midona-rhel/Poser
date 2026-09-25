using Poser.Config;
using Poser.Library;

namespace Poser.Application.Library;

public interface ILibraryFileOperations
{
    PoseLibraryFileActionResult Probe(string path);
    PoseLibraryFileActionResult Rename(string path, string name);
    PoseLibraryFileActionResult Move(string path, string directory);
    PoseLibraryFileActionResult Delete(string path);
    PoseLibraryFileActionResult Quarantine(string path);
    PoseLibraryFileActionResult EditMetadata(string path, string author, IReadOnlyList<string> tags,
        string description, PosePreviewImageEdit image);
    void SetFavorite(string path, bool favorite);
}

/// <summary>File-operation policy and favorite identity; Documents owns safe disk writes.</summary>
public sealed class LibraryFileOperations(ConfigurationService config, IPoseLibraryService library)
    : ILibraryFileOperations
{
    public PoseLibraryFileActionResult Probe(string path)
    {
        var result = PoseLibraryFileActions.Default.Probe(path);
        library.RequestScan();
        return result;
    }
    public PoseLibraryFileActionResult Rename(string path, string name) =>
        Changed(path, PoseLibraryFileActions.Default.Rename(path, name));
    public PoseLibraryFileActionResult Move(string path, string directory) =>
        Changed(path, PoseLibraryFileActions.Default.Move(path, directory));
    public PoseLibraryFileActionResult Delete(string path) =>
        Changed(path, PoseLibraryFileActions.Default.Delete(path));
    public PoseLibraryFileActionResult Quarantine(string path) =>
        Changed(path, PoseLibraryFileActions.Default.Quarantine(path));
    public PoseLibraryFileActionResult EditMetadata(string path, string author, IReadOnlyList<string> tags,
        string description, PosePreviewImageEdit image)
    {
        var result = PoseLibraryFileActions.Default.EditMetadata(path, author, tags, description, image);
        if (result.Succeeded) library.RequestScan();
        return result;
    }
    public void SetFavorite(string path, bool favorite)
    {
        var favorites = config.Config.Library.Favorites;
        bool changed = favorite ? favorites.Add(path) : favorites.Remove(path);
        if (changed) config.Save();
    }
    private PoseLibraryFileActionResult Changed(string path, PoseLibraryFileActionResult result)
    {
        if (!result.Succeeded) return result;
        var favorites = config.Config.Library.Favorites;
        if (favorites.Remove(path))
        {
            if (result.ResultPath != null && result.Kind != PoseLibraryFileActionKind.Quarantine)
                favorites.Add(result.ResultPath);
            config.Save();
        }
        library.RequestScan();
        return result;
    }
}
