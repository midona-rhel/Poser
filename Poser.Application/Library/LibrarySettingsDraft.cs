using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Poser.Domain.Library;
using Poser.Documents.Library;

namespace Poser.Application.Library;

public sealed class LibrarySourceDraft
{
    private string _name;
    private string _path;
    private bool _enabled;
    internal LibrarySourceConfig? Saved { get; }
    public int SavedIndex { get; }
    public LibrarySourceKind Kind { get; }
    public bool IsCustom => Kind == LibrarySourceKind.Custom;
    public string Name { get => _name; set { if (IsCustom) _name = value; } }
    public string Path { get => _path; set { if (IsCustom) _path = value; } }
    public bool Enabled { get => _enabled; set { if (IsCustom) _enabled = value; } }

    internal LibrarySourceDraft(LibrarySourceConfig source, LibrarySourceKind kind, int index)
    {
        _name = source.Name;
        _path = source.Path;
        _enabled = source.Enabled;
        Kind = kind;
        SavedIndex = index;
        // The saved list is never changed in place: Save replaces it whole.
        Saved = index < 0 ? null : source;
    }
}

public sealed record LibrarySourceIssue(LibrarySourceDraft Source, PoseLibrarySourceSnapshot Health, bool PendingSave);

/// <summary>
/// The Library page of the Settings window: the Poser folder and the source
/// folders as edited, before Save. Only the Settings window changes the
/// saved source list, so nothing else can change it while this draft is
/// open. The health shown for a row comes from the last library scan of the
/// saved folders, never from the draft.
/// </summary>
public sealed class LibrarySettingsDraft
{
    private readonly List<LibrarySourceDraft> _sources;
    private readonly LibrarySourceDraft[] _original;
    private readonly string _savedRoot;
    public string Root { get; set; }
    public IReadOnlyList<LibrarySourceDraft> Sources => _sources;
    public string EffectiveRoot => string.IsNullOrWhiteSpace(Root) ? LibraryConfiguration.DefaultRoot : Root.Trim();

    public LibrarySettingsDraft(LibraryConfiguration config)
    {
        Root = _savedRoot = config.ResolveRoot();
        _sources = config.Sources.Select((source, index) =>
            new LibrarySourceDraft(source, config.Classify(source), index)).ToList();
        _original = _sources.ToArray();
    }

    public LibrarySourceDraft Add(string name, string path)
    {
        var source = new LibrarySourceDraft(new LibrarySourceConfig
            { Name = name, Path = path, Kind = LibrarySourceKind.Custom }, LibrarySourceKind.Custom, -1);
        _sources.Add(source);
        return source;
    }

    public bool Remove(LibrarySourceDraft source) => source.IsCustom && _sources.Remove(source);

    public string PathFor(LibrarySourceDraft source) =>
        LibraryConfiguration.IsManaged(source.Kind) && EffectiveRoot != _savedRoot
            ? Path.Combine(EffectiveRoot, LibraryConfiguration.HomeLeaf(source.Kind)) : source.Path;

    public bool IsPending(LibrarySourceDraft source) => source.Saved is not { } saved
        || !_sources.Contains(source) || saved.Name != source.Name || saved.Path != PathFor(source)
        || saved.Enabled != source.Enabled;

    /// <summary>The scan's health of the saved folder this row started from.
    /// Null for a new row, and while the last scan still describes the source
    /// list from before the last Save: the window can open again before that
    /// Save's scan has finished.</summary>
    private static PoseLibrarySourceSnapshot? SavedHealth(LibrarySourceDraft source,
        PoseLibrarySnapshot snapshot)
    {
        if (source.Saved is not { } saved || source.SavedIndex >= snapshot.Sources.Count)
            return null;
        var health = snapshot.Sources[source.SavedIndex];
        return health.Index == source.SavedIndex && health.Name == saved.Name
            && health.Path == saved.Path && health.Enabled == saved.Enabled ? health : null;
    }

    public PoseLibrarySourceSnapshot? RowHealth(LibrarySourceDraft source,
        PoseLibrarySnapshot snapshot) =>
        IsPending(source) ? null : SavedHealth(source, snapshot);

    public static bool IsFailure(PoseLibrarySourceSnapshot? health) => health is { Enabled: true }
        && health.Health is PoseLibrarySourceHealth.Missing or PoseLibrarySourceHealth.Denied
            or PoseLibrarySourceHealth.Failed or PoseLibrarySourceHealth.Invalid;

    public IReadOnlyList<LibrarySourceIssue> Issues(PoseLibrarySnapshot snapshot) =>
        _original.Select(source => (source, health: SavedHealth(source, snapshot)))
            .Where(item => IsFailure(item.health))
            .Select(item => new LibrarySourceIssue(item.source, item.health!, IsPending(item.source))).ToArray();

    /// <summary>Whether Create folder is offered for a missing saved folder:
    /// a custom source, or one of Poser's own folders where it belongs under
    /// the saved Poser folder. A row with unsaved edits, and another tool's
    /// folder, are left alone.</summary>
    public bool CanRepair(LibrarySourceIssue issue) =>
        !IsPending(issue.Source)
        && issue.Health.Health == PoseLibrarySourceHealth.Missing
        && (issue.Source.IsCustom || (LibraryConfiguration.IsManaged(issue.Source.Kind)
            && LibraryConfiguration.SamePath(issue.Health.Path,
                Path.Combine(_savedRoot, LibraryConfiguration.HomeLeaf(issue.Source.Kind)))));

    /// <summary>Creates the missing saved folder. Create folder is only shown
    /// when <see cref="CanRepair"/> allows it.</summary>
    public static bool TryRepair(LibrarySourceIssue issue, out string detail) =>
        LibraryConfiguration.TryEnsureDirectory(issue.Health.Path, out detail);

    public bool TryApply(LibraryConfiguration live, out string detail)
    {
        try
        {
            if (!Path.IsPathFullyQualified(EffectiveRoot))
                throw new ArgumentException("Choose an absolute Poser folder path.");
            _ = Path.GetFullPath(EffectiveRoot);
        }
        catch (Exception ex)
        {
            detail = "Poser folder is invalid: " + ex.Message;
            return false;
        }
        var sources = new List<LibrarySourceConfig>();
        foreach (var source in _sources)
            sources.Add(new LibrarySourceConfig
            {
                Name = source.Name, Path = PathFor(source), Enabled = source.Enabled,
                Kind = source.Kind,
            });
        var candidate = new LibraryConfiguration { Sources = sources };
        // The common root is persisted through the managed poses home. Do not
        // accept a root that Explorer/export resolution would silently ignore.
        if (!LibraryConfiguration.SamePath(EffectiveRoot, _savedRoot)
            && !LibraryConfiguration.SamePath(candidate.ResolveRoot(), EffectiveRoot))
        {
            detail = "The Poser poses home is missing or ambiguous, so the new root cannot be saved. "
                + "Restore the previous Poser folder value to save other edits, or Cancel; no sources were changed.";
            return false;
        }
        live.Sources = sources;
        detail = string.Empty;
        return true;
    }
}
