using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Poser.Config;

namespace Poser.Application.Tests.Settings;

public sealed class ConfigurationRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "poser-config-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "Poser.json");

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Unreadable_config_is_backed_up_before_defaults_can_replace_it(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);
        var store = new ConfigurationFileStore(FilePath);
        var result = store.Load();
        Assert.NotEmpty(result.Failure);
        var backup = Assert.Single(Directory.GetFiles(_directory, "*.bak-*"));
        Assert.Equal(content, File.ReadAllText(backup));
        store.Save(result.Configuration);
        Assert.Equal(content, File.ReadAllText(backup));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void First_run_and_saved_preferences_do_not_enter_recovery()
    {
        var store = new ConfigurationFileStore(FilePath);
        Assert.Empty(store.Load().Failure);
        var config = new PoserConfiguration { UndoDepth = 37 };
        config.UI.SectionDisclosure["Appearance"] = false;
        config.UI.Bindings["Undo"] = new("Ctrl+Z", "Ctrl+U");
        store.Save(config);
        var loaded = store.Load();
        Assert.Empty(loaded.Failure);
        Assert.Equal(37, loaded.Configuration.UndoDepth);
        Assert.False(loaded.Configuration.UI.SectionDisclosure["Appearance"]);
        Assert.Equal("Ctrl+U", loaded.Configuration.UI.Bindings["Undo"].Secondary);
    }

    [Fact]
    public void Legacy_type_metadata_dictionaries_and_collection_wrappers_load_without_Core()
    {
        Directory.CreateDirectory(_directory);
        var config = new PoserConfiguration();
        config.UI.Bindings["Undo"] = new("Ctrl+OEM_4", "");
        config.UI.DetachedPlacements["Inspector"] = new(new(12, 34), new(350, 600));
        config.BoneSymmetryOverrides["j_te_l"] = Poser.Services.SymmetryMode.Mirror;
        config.Skeleton.BoneVisibilityPresets.Add(new() { Name = "Saved", Bones = ["j_te_l"] });
        var json = JsonConvert.SerializeObject(config, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });
        json = json.Replace(", Poser.Documents", ", Poser.Core").Replace(", Poser.Domain", ", Poser.Core");
        File.WriteAllText(FilePath, json);
        var loaded = new ConfigurationFileStore(FilePath).Load();
        Assert.Empty(loaded.Failure);
        Assert.Equal("Ctrl+[", KeyChord.Parse(loaded.Configuration.UI.Bindings["Undo"].Primary).ToString());
        Assert.Equal(new System.Numerics.Vector2(12, 34), loaded.Configuration.UI.DetachedPlacements["Inspector"].Position);
        Assert.Equal(Poser.Services.SymmetryMode.Mirror, loaded.Configuration.BoneSymmetryOverrides["j_te_l"]);
        Assert.Contains(loaded.Configuration.Skeleton.BoneVisibilityPresets, p => p.Name == "Saved" && p.Bones.Contains("j_te_l"));
    }

    [Fact]
    public void Quiet_save_and_setting_change_publish_the_expected_notifications()
    {
        var persistence = new MemoryPersistence();
        var settings = new ConfigurationService(persistence);
        int notifications = 0;
        settings.OnConfigurationChanged += () => notifications++;
        settings.Save(notify: false);
        Assert.Equal(1, persistence.Saves);
        Assert.Equal(0, notifications);
        settings.ApplyChange();
        Assert.Equal(2, persistence.Saves);
        Assert.Equal(1, notifications);
    }

    private sealed class MemoryPersistence : IConfigurationPersistence
    {
        public int Saves;
        public ConfigurationLoadResult Load() => new(new PoserConfiguration());
        public void Save(PoserConfiguration configuration) => Saves++;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
