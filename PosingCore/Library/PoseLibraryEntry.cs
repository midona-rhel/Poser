using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Library;

/// <summary>
/// What a scanned file IS. The browser shows one kind at a time, so this is the
/// dimension its type tabs filter on.
/// </summary>
public enum PoseLibraryEntryKind : byte
{
    /// <summary>A <c>.pose</c> or Anamnesis <c>.cmp</c>.</summary>
    Pose,

    /// <summary>A Mare character file. Carries no pose metadata at all — no
    /// author, no tags, no preview — so the scan never opens one.</summary>
    Mcdf,

    /// <summary>An <c>.xivs</c> whole scene. Its metadata is observed
    /// through the scene codec, so a listing can never advertise a scene the
    /// load would reject.</summary>
    Scene,

    /// <summary>An <c>.xiva</c> actor entry: the scene container restricted
    /// to one actor. Read through the same codec as a scene.</summary>
    Actor,

    /// <summary>An <c>.xivl</c> light document.</summary>
    Light,

    /// <summary>An <c>.xivc</c> camera document.</summary>
    Camera,
}

/// <summary>
/// The ONE way Poser writes a timestamp a user reads. Two-digit year: a tile
/// caption, a rail heading and a metadata line are all narrow, and the century
/// is the least informative part of a date nobody reads from 1900. Sortable
/// order (year first) is kept, because these strings sit in lists.
/// <para>A format that names a file or folder ON DISK is NOT one of these —
/// those stay four-digit, because a stored name is parsed back and a two-digit
/// year is ambiguous forever.</para>
/// </summary>
public static class LibraryStamp
{
    /// <summary>Day and time: every tile, rail entry and metadata line.</summary>
    public const string DateTimeFormat = "yy-MM-dd HH:mm";

    /// <summary>Day alone: an auto-save rail day, a scene section heading.
    /// </summary>
    public const string DateFormat = "yy-MM-dd";
}

/// <summary>Why a pose metadata probe did or did not produce metadata.</summary>
public enum PoseLibraryMetadataStatus : byte
{
    Valid,
    Corrupt,
    Future,
    Oversized,
}

/// <summary>
/// One library file the scan found. Every string the browser reads per frame is
/// minted here, at scan time, because the grid touches all of them on each
/// keystroke and may allocate nothing while doing it.
/// </summary>
public sealed class PoseLibraryEntry
{
    public required PoseLibraryEntryKind Kind { get; init; }

    /// <summary>Absolute path of the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name without its extension.</summary>
    public required string Name { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Name"/> for search.</summary>
    public required string NameLower { get; init; }

    /// <summary>Last write time pre-formatted through
    /// <see cref="LibraryStamp.DateTimeFormat"/>.</summary>
    public required string ModifiedText { get; init; }

    public required DateTime Modified { get; init; }

    /// <summary>Index into the owning snapshot's <c>Folders</c> list.</summary>
    public required int Folder { get; init; }

    public string? Author { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Author"/> for search;
    /// empty when the file names no author.</summary>
    public string AuthorLower { get; init; } = string.Empty;

    private IReadOnlyList<string> _tags = Array.Empty<string>();
    private IReadOnlyList<string> _tagsLower = Array.Empty<string>();

    public IReadOnlyList<string> Tags
    {
        get => _tags;
        init => _tags = Freeze(value);
    }

    /// <summary>Invariant lowercase copies of <see cref="Tags"/>, same order.</summary>
    public IReadOnlyList<string> TagsLower
    {
        get => _tagsLower;
        init => _tagsLower = Freeze(value);
    }

    public PoseLibraryMetadataStatus MetadataStatus { get; init; }

    /// <summary>Short diagnostic suitable for a future recovery surface.</summary>
    public string MetadataDetail { get; init; } = string.Empty;

    /// <summary>An Anamnesis <c>.cmp</c> file; carries no metadata.</summary>
    public bool IsLegacy { get; init; }

    /// <summary>What a <see cref="PoseLibraryEntryKind.Scene"/> entry holds,
    /// pre-formatted at scan time (e.g. "3 actors, 2 lights"). Empty for
    /// every other kind.</summary>
    public string SceneContents { get; init; } = string.Empty;

    /// <summary>Where a <see cref="PoseLibraryEntryKind.Scene"/> entry was
    /// captured, as its own document recorded it. Empty for every other kind,
    /// and for a scene written before scenes recorded a place — a listing
    /// groups those by their day alone rather than inventing a place.</summary>
    public string ScenePlace { get; init; } = string.Empty;

    /// <summary>When a <see cref="PoseLibraryEntryKind.Scene"/> entry says it
    /// was captured, as its own document recorded it. Null for every other
    /// kind, and for a scene written before scenes recorded it — a listing
    /// then falls back to <see cref="Modified"/>, which is when the FILE last
    /// changed rather than when the scene was taken. Both halves of a "where
    /// and when" heading must come from the document wherever the document
    /// answers, or a copied file files under a day it was never captured on.
    /// </summary>
    public DateTimeOffset? SceneCapturedAt { get; init; }

    /// <summary>A <c>.pose</c> file with a non-empty embedded preview image.</summary>
    public bool HasThumbnail { get; init; }

    private static IReadOnlyList<string> Freeze(IReadOnlyList<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>()).ToArray());
}

/// <summary>
/// One node of the flattened folder tree. Roots are the configured sources.
/// </summary>
public sealed class PoseLibraryFolder
{
    /// <summary>
    /// <c>"&lt;sourceIndex&gt;|&lt;relative dir&gt;"</c>; the relative part is
    /// empty for a source root.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Source name for a root, directory name for a subfolder.</summary>
    public required string Label { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Label"/> for search.</summary>
    public required string LabelLower { get; init; }

    /// <summary>0 for a source root, +1 per nesting level.</summary>
    public required int Depth { get; init; }

    /// <summary>Recursive file count at and below this folder, both kinds.
    /// </summary>
    public int Count { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Pose"/> count at and
    /// below this folder.</summary>
    public int PoseCount { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Mcdf"/> count at and
    /// below this folder. Both per-kind counts are recursive, so a folder with
    /// none of a kind has no descendant of that kind either — which is what
    /// lets a browser tab drop the whole subtree and keep a valid tree.
    /// </summary>
    public int McdfCount { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Scene"/> count at
    /// and below this folder, on the same recursive contract.</summary>
    public int SceneCount { get; init; }
}

/// <summary>
/// One immutable result of a scan. Readers take the whole snapshot in a single
/// reference read; it is never mutated after publication.
/// </summary>
public sealed class PoseLibrarySnapshot
{
    public required int Revision { get; init; }

    /// <summary>Sorted by folder order, then <c>NameLower</c> ordinal.</summary>
    private IReadOnlyList<PoseLibraryEntry> _entries = Array.Empty<PoseLibraryEntry>();
    private IReadOnlyList<PoseLibraryFolder> _folders = Array.Empty<PoseLibraryFolder>();

    public required IReadOnlyList<PoseLibraryEntry> Entries
    {
        get => _entries;
        init => _entries = Freeze(value);
    }

    /// <summary>Flattened depth-first in display order.</summary>
    public required IReadOnlyList<PoseLibraryFolder> Folders
    {
        get => _folders;
        init => _folders = Freeze(value);
    }

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<T>()).ToArray());
}
