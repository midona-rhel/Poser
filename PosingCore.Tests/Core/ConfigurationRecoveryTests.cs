using System;
using System.IO;
using Dalamud.Plugin;
using NSubstitute;
using Poser.Config;

namespace Poser.Tests.Core;

public sealed class ConfigurationRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "poser-config-recovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unreadable_or_wrong_shape_config_is_backed_up_but_first_run_is_not_a_failure()
    {
        var throwing = CreatePlugin(() => throw new InvalidOperationException("bad json"), true);
        var unreadable = new ConfigurationService(throwing);
        Assert.NotEmpty(unreadable.LoadFailure);
        Assert.Single(Directory.GetFiles(_directory, "Poser.json.bak-*"));

        Directory.Delete(_directory, recursive: true);
        var wrongShape = new ConfigurationService(CreatePlugin(() => null, true));
        Assert.NotEmpty(wrongShape.LoadFailure);
        Assert.Single(Directory.GetFiles(_directory, "Poser.json.bak-*"));

        Directory.Delete(_directory, recursive: true);
        var firstRun = new ConfigurationService(CreatePlugin(() => null, false));
        Assert.Empty(firstRun.LoadFailure);
        Assert.Empty(Directory.GetFiles(_directory, "Poser.json.bak-*"));
    }

    [Fact]
    public void Readable_config_and_new_defaults_are_preserved_without_recovery_noise()
    {
        var stored = new PoserConfiguration { UndoDepth = 37 };
        var service = new ConfigurationService(CreatePlugin(() => stored, true));

        Assert.Empty(service.LoadFailure);
        Assert.Equal(37, service.Config.UndoDepth);
        Assert.NotNull(service.Config.Camera);
        Assert.Empty(Directory.GetFiles(_directory, "Poser.json.bak-*"));
    }

    private IDalamudPluginInterface CreatePlugin(Func<object?> read, bool writeFile)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "Poser.json");
        if (writeFile)
            File.WriteAllText(path, "{ this is not a config }");
        var plugin = Substitute.For<IDalamudPluginInterface>();
        plugin.GetPluginConfig().Returns(_ => read());
        plugin.ConfigFile.Returns(new FileInfo(path));
        return plugin;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
