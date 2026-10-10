using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Poser.Config;
using Poser.Domain.Preferences;

namespace Poser.Application.Tests.Settings;

public sealed class ConfigurationRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "poser-config-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "Poser.json");

    [Theory]
    [InlineData("{ broken")]
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
    public void Legacy_type_metadata_dictionaries_and_collection_wrappers_load_without_Core()
    {
        Directory.CreateDirectory(_directory);
        var config = new PoserConfiguration();
        config.UI.Bindings["Undo"] = new("Ctrl+OEM_4", "");
        config.UI.DetachedPlacements["Inspector"] = new(new(12, 34), new(350, 600));
        config.BoneSymmetryOverrides["j_te_l"] = Poser.Domain.Preferences.SymmetryMode.Mirror;
        config.Skeleton.BoneVisibilityPresets.Add(new() { Name = "Saved", Bones = ["j_te_l"] });
        var json = JsonConvert.SerializeObject(config, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });
        json = json.Replace(", Poser.Documents", ", Poser.Core").Replace(", Poser.Domain", ", Poser.Core");
        File.WriteAllText(FilePath, json);
        var loaded = new ConfigurationFileStore(FilePath).Load();
        Assert.Empty(loaded.Failure);
        Assert.Equal("Ctrl+[", KeyChord.Parse(loaded.Configuration.UI.Bindings["Undo"].Primary).ToString());
        Assert.Equal(new System.Numerics.Vector2(12, 34), loaded.Configuration.UI.DetachedPlacements["Inspector"].Position);
        Assert.Equal(Poser.Domain.Preferences.SymmetryMode.Mirror, loaded.Configuration.BoneSymmetryOverrides["j_te_l"]);
        Assert.Contains(loaded.Configuration.Skeleton.BoneVisibilityPresets, p => p.Name == "Saved" && p.Bones.Contains("j_te_l"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
