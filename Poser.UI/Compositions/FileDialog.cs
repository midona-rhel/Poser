using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Library;

namespace Poser.UI;

/// <summary>
/// What a preview provider answers with. The dialog owns no rendering: a
/// provider resolves the selection into a host texture and its natural size,
/// and the panel aspect-fits that into whatever column the frame left it.
/// A provider that has nothing to show answers <c>null</c>, and the column
/// does not exist that frame.
/// </summary>
public readonly record struct FilePreviewResult(
    nint Texture, Vector2 Size, string? Caption);

/// <summary>
/// One caller-drawn column right of the file list. The dialog owns the
/// geometry and nothing else: the panel is handed its box in screen space and
/// the full path the list is HIGHLIGHTING — null for a folder row, and for no
/// selection at all — and whatever it draws there is its own business,
/// scrolling included.
/// </summary>
/// <param name="Width">Logical column width. Unlike
/// <see cref="FileDialog.FilePreview"/>, which steals width from the
/// listing, this is ADDED to the dialog: the browser keeps its own width
/// whatever a consumer bolts on beside it.</param>
public readonly record struct FileSidePanel(
    float Width, Action<Vector2, Vector2, string?> Draw);

/// <summary>One listing row, as the dialog sees it.</summary>
internal readonly record struct FileListingEntry(
    string Name, string FullPath, bool IsDirectory, DateTime Modified);

/// <summary>One quick-menu destination.</summary>
internal readonly record struct FileQuickEntry(
    string Name, string Path, TablerIcon Icon);

/// <summary>
/// THE DIALOG'S VIEW OF THE DISK, and the only one. Ordering, extension
/// filtering and the error line are the DIALOG's policy and stay above this
/// line; hidden-attribute suppression is the filesystem's own and stays below.
/// </summary>
internal interface IFileListingSource
{
    /// <summary>Fills <paramref name="into"/> with the folder's contents, in
    /// no particular order. Throwing is the contract for an unreadable folder:
    /// the dialog turns the message into its error line.</summary>
    void Enumerate(string path, List<FileListingEntry> into);

    void QuickAccess(List<FileQuickEntry> into);

    bool DirectoryExists(string path);

    string? Parent(string path);

    string DefaultPath { get; }
}
