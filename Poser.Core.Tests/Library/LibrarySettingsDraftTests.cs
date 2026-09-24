using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Poser.Library;

namespace Poser.Tests.Library;

public sealed class LibrarySettingsDraftTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("PoserLibrarySettings-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LibraryConfiguration Quartet(string name = "Custom Poser Root") => new()
    {
        Sources = LibraryConfiguration.Homes.Select(home => new LibrarySourceConfig
        {
            Name = home.Name,
            Path = Path.Combine(_root, name, LibraryConfiguration.HomeLeaf(LibraryConfiguration.HomeKind(home.Name))),
        }).ToList(),
        PoseRootSeeded = true, SceneRootSeeded = true, ObjectsRootSeeded = true, McdfRootSeeded = true,
        DefaultsSeeded = true,
    };

    private LibraryConfiguration Custom() => new()
    {
        Sources = [new LibrarySourceConfig { Name = "Custom", Path = Path.Combine(_root, "missing"), Kind = LibrarySourceKind.Custom }],
    };

    private static PoseLibrarySnapshot Snapshot(LibraryConfiguration config, params PoseLibrarySourceHealth[] states) => new()
    {
        Revision = 1, Generation = 1, TerminalResult = PoseLibraryScanResult.PartialFailure,
        Folders = [], Entries = [],
        Sources = config.Sources.Select((source, i) => new PoseLibrarySourceSnapshot
        {
            Index = i, Name = source.Name, Path = source.Path, Enabled = source.Enabled,
            Health = states.Length > i ? states[i] : PoseLibrarySourceHealth.Missing,
            Detail = "Test reason for " + source.Path,
        }).ToArray(),
    };

    [Fact]
    public void Legacy_json_quartet_is_recognized_without_rewriting_paths_or_creating_folders()
    {
        var original = Quartet();
        var json = JsonConvert.SerializeObject(new
        {
            Sources = original.Sources.Select(s => new { s.Name, s.Path, s.Enabled }),
        });
        Assert.DoesNotContain("Kind", json);
        var config = JsonConvert.DeserializeObject<LibraryConfiguration>(json)!;
        Assert.All(config.Sources, s => Assert.Equal(LibrarySourceKind.Legacy, s.Kind));
        Assert.All(config.Sources, s => Assert.Equal(LibraryConfiguration.HomeKind(s.Name), config.Classify(s)));
        Assert.Equal(Path.Combine(_root, "Custom Poser Root"), config.ResolveRoot());
        Assert.Equal(original.Sources.Select(s => s.Path), config.Sources.Select(s => s.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Default_external_legacy_identity_requires_exact_name_and_normalized_path()
    {
        var expected = Path.Combine(_root, "Brio", "Poses");
        var config = new LibraryConfiguration { Sources =
        [
            new() { Name = "Brio Poses", Path = Path.Combine(_root, "Brio", ".", "Poses") + Path.DirectorySeparatorChar },
            new() { Name = "Brio Poses", Path = Path.Combine(_root, "custom brio") },
            new() { Name = "Other name", Path = expected },
            new() { Name = "Anamnesis Poses", Path = "bad\0path" },
        ] };
        Assert.Equal(LibrarySourceKind.Brio, config.Classify(config.Sources[0], _root));
        Assert.All(config.Sources.Skip(1), s => Assert.Equal(LibrarySourceKind.Custom, config.Classify(s, _root)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Conflicting_duplicates_multiple_roots_and_incomplete_layouts_stay_custom()
    {
        var duplicate = Quartet();
        duplicate.Sources.Add(new() { Name = duplicate.Sources[0].Name, Path = duplicate.Sources[0].Path });
        Assert.All(duplicate.Sources, s => Assert.Equal(LibrarySourceKind.Custom, duplicate.Classify(s)));
        var multiple = Quartet();
        multiple.Sources.AddRange(Quartet("Other Root").Sources);
        Assert.All(multiple.Sources, s => Assert.Equal(LibrarySourceKind.Custom, multiple.Classify(s)));
        var incomplete = Quartet();
        incomplete.Sources.RemoveAt(0);
        Assert.All(incomplete.Sources, s => Assert.Equal(LibrarySourceKind.Custom, incomplete.Classify(s)));
        var external = new LibraryConfiguration { Sources =
        [
            new() { Name = "Brio Poses", Path = Path.Combine(_root, "Brio", "Poses") },
            new() { Name = "Brio Poses", Path = Path.Combine(_root, "Brio", "Poses") },
        ] };
        Assert.All(external.Sources, s => Assert.Equal(LibrarySourceKind.Custom, external.Classify(s, _root)));
    }

    [Fact]
    public void Root_change_and_repeated_save_preserve_same_name_custom_and_external_records()
    {
        var config = Quartet();
        config.Sources.Add(new() { Name = LibraryConfiguration.ObjectsSourceName, Path = Path.Combine(_root, "legacy separate objects"), Enabled = false });
        config.Sources.Add(new() { Name = "Brio Poses", Path = Path.Combine(_root, "legacy separate brio") });
        var preserved = config.Sources.Skip(4).Select(s => (s.Name, s.Path, s.Enabled)).ToArray();
        var draft = new LibrarySettingsDraft(config) { Root = Path.Combine(_root, "New Managed Root") };
        Assert.All(draft.Sources.Take(4), s => Assert.False(s.IsCustom));
        Assert.All(draft.Sources.Skip(4), s => Assert.True(s.IsCustom));
        Assert.True(draft.TryApply(config, out var detail), detail);
        Assert.Equal(draft.Root, config.ResolveRoot());
        Assert.All(config.Sources.Take(4), s => Assert.Equal(Path.Combine(draft.Root, LibraryConfiguration.HomeLeaf(s.Kind)), s.Path));
        for (int i = 0; i < 2; i++)
        {
            config.EnsureDefaults(_root);
            Assert.True(new LibrarySettingsDraft(config).TryApply(config, out detail), detail);
        }
        Assert.Equal(6, config.Sources.Count);
        Assert.Equal(preserved, config.Sources.Skip(4).Select(s => (s.Name, s.Path, s.Enabled)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Changed_root_without_recognized_poses_is_refused_without_mutating_live_configuration(int layout)
    {
        var config = Quartet();
        if (layout == 2)
            foreach (var source in config.Sources)
                source.Kind = LibraryConfiguration.HomeKind(source.Name);
        // Missing legacy poses, incomplete legacy quartet with a poses-named
        // custom record, and a deleted explicitly managed poses home.
        config.Sources.RemoveAt(layout == 1 ? 1 : 0);
        config.Sources.Add(new() { Name = "Custom", Path = Path.Combine(_root, "custom"), Kind = LibrarySourceKind.Custom });
        config.IconSize = 144;
        var originalList = config.Sources;
        var originalRows = config.Sources.ToArray();
        string before = JsonConvert.SerializeObject(config);
        var draft = new LibrarySettingsDraft(config) { Root = Path.Combine(_root, "Rejected Root") };
        draft.Sources.Last().Enabled = false;
        draft.Add("Pending addition", Path.Combine(_root, "pending addition"));

        Assert.False(draft.TryApply(config, out var detail));
        Assert.Contains("Poser poses home", detail);
        Assert.Contains("Cancel", detail);
        Assert.Same(originalList, config.Sources);
        for (int i = 0; i < originalRows.Length; i++)
            Assert.Same(originalRows[i], config.Sources[i]);
        Assert.Equal(before, JsonConvert.SerializeObject(config));
        Assert.Equal(LibraryConfiguration.DefaultRoot, config.ResolveRoot());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Root_round_trip_uses_normalized_paths_for_recognized_and_unchanged_roots()
    {
        var config = Quartet();
        string root = Path.Combine(_root, "New Root");
        var draft = new LibrarySettingsDraft(config) { Root = Path.Combine(root, ".") + Path.DirectorySeparatorChar };
        Assert.True(draft.TryApply(config, out var detail), detail);
        Assert.True(LibraryConfiguration.SamePath(root, config.ResolveRoot()));

        var noManagedHome = Custom();
        draft = new LibrarySettingsDraft(noManagedHome)
            { Root = Path.Combine(noManagedHome.ResolveRoot(), ".") + Path.DirectorySeparatorChar };
        draft.Sources[0].Enabled = false;
        Assert.True(draft.TryApply(noManagedHome, out detail), detail);
        Assert.False(noManagedHome.Sources[0].Enabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void New_seeds_and_additions_have_explicit_ownership_and_builtin_rows_cannot_be_edited_or_removed()
    {
        var config = new LibraryConfiguration();
        config.EnsureDefaults(_root);
        config.EnsureDefaults(_root);
        Assert.Equal(6, config.Sources.Count);
        var draft = new LibrarySettingsDraft(config);
        foreach (var source in draft.Sources)
        {
            var original = (source.Name, source.Path, source.Enabled);
            source.Name = "renamed";
            source.Path = Path.Combine(_root, "redirected");
            source.Enabled = false;
            Assert.Equal(original, (source.Name, source.Path, source.Enabled));
            Assert.False(draft.Remove(source));
        }
        var custom = draft.Add(LibraryConfiguration.ObjectsSourceName, Path.Combine(_root, "user objects"));
        Assert.True(custom.IsCustom);
        custom.Name = "edited";
        custom.Enabled = false;
        Assert.True(draft.TryApply(config, out var detail), detail);
        Assert.Equal(LibrarySourceKind.Custom, config.Sources.Last().Kind);
        Assert.Equal("edited", config.Sources.Last().Name);
        Assert.False(config.Sources.Last().Enabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Draft_path_changes_never_relabel_saved_health_and_cancel_discards_only_draft()
    {
        var config = Custom();
        var snapshot = Snapshot(config);
        var draft = new LibrarySettingsDraft(config);
        var row = draft.Sources[0];
        Assert.True(LibrarySettingsDraft.IsFailure(draft.RowHealth(row, snapshot, config)));
        string savedPath = row.Path;
        row.Path = Path.Combine(_root, "draft path");
        Assert.Equal(savedPath, config.Sources[0].Path);
        Assert.Null(draft.RowHealth(row, snapshot, config));
        var issue = Assert.Single(draft.Issues(snapshot, config));
        Assert.True(issue.PendingSave);
        Assert.Equal(savedPath, issue.Health.Path);
        Assert.False(draft.CanRepair(issue, config));
        var reopened = new LibrarySettingsDraft(config);
        Assert.Equal(savedPath, reopened.Sources[0].Path);
        Assert.False(Assert.Single(reopened.Issues(snapshot, config)).PendingSave);
        Assert.True(draft.TryApply(config, out var detail), detail);
        reopened = new LibrarySettingsDraft(config);
        Assert.Null(reopened.RowHealth(reopened.Sources[0], snapshot, config));
        Assert.Empty(reopened.Issues(snapshot, config));
        Assert.Single(reopened.Issues(Snapshot(config), config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Custom_disable_or_remove_is_pending_until_save_then_clears_issue(bool remove)
    {
        var config = Custom();
        var snapshot = Snapshot(config);
        var draft = new LibrarySettingsDraft(config);
        if (remove)
            Assert.True(draft.Remove(draft.Sources[0]));
        else
            draft.Sources[0].Enabled = false;
        Assert.True(config.Sources[0].Enabled);
        Assert.True(Assert.Single(draft.Issues(snapshot, config)).PendingSave);
        Assert.True(draft.TryApply(config, out var detail), detail);
        Assert.Empty(new LibrarySettingsDraft(config).Issues(snapshot, config));
    }

    [Fact]
    public void Details_include_only_enabled_failed_states_and_resolve_after_retry()
    {
        var config = Custom();
        for (int i = 1; i < 7; i++)
            config.Sources.Add(new() { Name = "Source " + i, Path = Path.Combine(_root, i.ToString()) });
        config.Sources[5].Enabled = false;
        var snapshot = Snapshot(config, PoseLibrarySourceHealth.Missing, PoseLibrarySourceHealth.Denied,
            PoseLibrarySourceHealth.Failed, PoseLibrarySourceHealth.Invalid, PoseLibrarySourceHealth.Ready,
            PoseLibrarySourceHealth.Disabled, PoseLibrarySourceHealth.Unscanned);
        var draft = new LibrarySettingsDraft(config);
        Assert.Equal(4, draft.Issues(snapshot, config).Count);
        Assert.Empty(draft.Issues(Snapshot(config, Enumerable.Repeat(PoseLibrarySourceHealth.Ready, 7).ToArray()), config));
    }

    [Fact]
    public void Repair_checks_saved_identity_and_never_creates_system_external_references()
    {
        var config = new LibraryConfiguration();
        config.EnsureDefaults(_root);
        var draft = new LibrarySettingsDraft(config);
        var issues = draft.Issues(Snapshot(config), config);
        Assert.True(draft.TryRepair(issues[0], config, out var detail), detail);
        Assert.True(Directory.Exists(issues[0].Health.Path));
        foreach (var issue in issues.Skip(4))
        {
            Assert.False(draft.CanRepair(issue, config));
            Assert.False(draft.TryRepair(issue, config, out _));
            Assert.False(Directory.Exists(issue.Health.Path));
        }
        var next = issues[1];
        config.Sources[1].Path = Path.Combine(_root, "concurrent path");
        Assert.False(draft.TryRepair(next, config, out _));
        Assert.False(Directory.Exists(next.Health.Path));
        Assert.False(draft.TryApply(config, out detail));
        Assert.Contains("changed while Settings was open", detail);
    }

    [Fact]
    public void Failed_custom_repair_reports_refusal_and_cancel_does_not_undo_explicit_repair()
    {
        var config = Custom();
        string blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        config.Sources[0].Path = Path.Combine(blocker, "child");
        var draft = new LibrarySettingsDraft(config);
        Assert.False(draft.TryRepair(Assert.Single(draft.Issues(Snapshot(config), config)), config, out var detail));
        Assert.Contains(config.Sources[0].Path, detail);
        config.Sources[0].Path = Path.Combine(_root, "repairable");
        draft = new LibrarySettingsDraft(config);
        Assert.True(draft.TryRepair(Assert.Single(draft.Issues(Snapshot(config), config)), config, out detail), detail);
        var reopened = new LibrarySettingsDraft(config);
        Assert.True(Directory.Exists(reopened.Sources[0].Path));
        Assert.Empty(reopened.Issues(Snapshot(config, PoseLibrarySourceHealth.Ready), config));
    }
}
