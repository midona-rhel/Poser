using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Poser.Domain.Library;
using Poser.Documents.Library;
using Poser.Application.Library;

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
}
