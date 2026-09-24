using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Poser.Library;

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
        Saved = index < 0 ? null : LibrarySettingsDraft.Copy(source);
    }
}

public sealed record LibrarySourceIssue(LibrarySourceDraft Source, PoseLibrarySourceSnapshot Health, bool PendingSave);

/// <summary>Settings' source transaction. Health describes saved identities;
/// source edits are never previewed into live configuration.</summary>
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

    private static bool Matches(LibrarySourceConfig a, LibrarySourceConfig b) =>
        a.Name == b.Name && a.Path == b.Path && a.Enabled == b.Enabled && a.Kind == b.Kind;

    public bool StillSaved(LibrarySourceDraft source, LibraryConfiguration live) =>
        source.Saved is { } saved && source.SavedIndex >= 0 && source.SavedIndex < live.Sources.Count
        && Matches(saved, live.Sources[source.SavedIndex])
        && live.Classify(live.Sources[source.SavedIndex]) == source.Kind;

    private PoseLibrarySourceSnapshot? SavedHealth(LibrarySourceDraft source,
        PoseLibrarySnapshot snapshot, LibraryConfiguration live)
    {
        if (!StillSaved(source, live) || source.SavedIndex >= snapshot.Sources.Count)
            return null;
        var health = snapshot.Sources[source.SavedIndex];
        var saved = source.Saved!;
        return health.Index == source.SavedIndex && health.Name == saved.Name
            && health.Path == saved.Path && health.Enabled == saved.Enabled ? health : null;
    }

    public PoseLibrarySourceSnapshot? RowHealth(LibrarySourceDraft source,
        PoseLibrarySnapshot snapshot, LibraryConfiguration live) =>
        IsPending(source) ? null : SavedHealth(source, snapshot, live);

    public static bool IsFailure(PoseLibrarySourceSnapshot? health) => health is { Enabled: true }
        && health.Health is PoseLibrarySourceHealth.Missing or PoseLibrarySourceHealth.Denied
            or PoseLibrarySourceHealth.Failed or PoseLibrarySourceHealth.Invalid;

    public IReadOnlyList<LibrarySourceIssue> Issues(PoseLibrarySnapshot snapshot, LibraryConfiguration live) =>
        _original.Select(source => (source, health: SavedHealth(source, snapshot, live)))
            .Where(item => IsFailure(item.health))
            .Select(item => new LibrarySourceIssue(item.source, item.health!, IsPending(item.source))).ToArray();

    public bool CanRepair(LibrarySourceIssue issue, LibraryConfiguration live) =>
        IsFailure(issue.Health) && !IsPending(issue.Source) && StillSaved(issue.Source, live)
        && issue.Health.Health == PoseLibrarySourceHealth.Missing
        && issue.Health.Index == issue.Source.SavedIndex && issue.Health.Path == issue.Source.Saved!.Path
        && issue.Health.Name == issue.Source.Saved.Name && issue.Health.Enabled == issue.Source.Saved.Enabled
        && (issue.Source.IsCustom || (LibraryConfiguration.IsManaged(issue.Source.Kind)
            && LibraryConfiguration.SamePath(issue.Health.Path,
                Path.Combine(live.ResolveRoot(), LibraryConfiguration.HomeLeaf(issue.Source.Kind)))));

    public bool TryRepair(LibrarySourceIssue issue, LibraryConfiguration live, out string detail)
    {
        if (!CanRepair(issue, live))
        {
            detail = "This source changed or is system-provided. Save or cancel pending edits and retry.";
            return false;
        }
        return LibraryConfiguration.TryEnsureDirectory(issue.Health.Path, out detail);
    }

    public bool TryApply(LibraryConfiguration live, out string detail)
    {
        // A concurrent source change must not be overwritten by a stale draft.
        if (_original.Length != live.Sources.Count || _original.Any(source => !StillSaved(source, live)))
        {
            detail = "Library sources changed while Settings was open. Cancel and reopen Settings before saving.";
            return false;
        }
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

    internal static LibrarySourceConfig Copy(LibrarySourceConfig source) => new()
        { Name = source.Name, Path = source.Path, Enabled = source.Enabled, Kind = source.Kind };
}
