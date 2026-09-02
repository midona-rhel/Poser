using System;

namespace Poser.UI.Controls;

/// <summary>The folder a surface's file dialogs reopen in: the last chosen
/// file's folder, seeded from a start folder. One per surface, shared by
/// its save and load dialogs.</summary>
public sealed class RememberedFolder
{
    public string Path { get; private set; }

    public RememberedFolder(string start) => Path = start;

    public void Remember(string filePath) =>
        Path = System.IO.Path.GetDirectoryName(filePath) ?? Path;

    /// <summary>Opens the dialog here; the chosen file's folder is
    /// remembered before the callback runs.</summary>
    public void Open(Crystarium.FileDialog dialog, Action<string> chosen) =>
        dialog.Open(Path, path =>
        {
            Remember(path);
            chosen(path);
        });
}
