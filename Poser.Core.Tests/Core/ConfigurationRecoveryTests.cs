using System;
using System.IO;
using Dalamud.Plugin;
using NSubstitute;
using Poser.Config;

namespace Poser.Tests.Core;

/// <summary>
/// The one promise the config load makes: settings it cannot read are
/// PRESERVED and reported, never silently replaced. Ktisis's mechanism —
/// back the file up, carry on with defaults, say so.
/// </summary>
public class ConfigurationRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "poser-config-recovery-" + Guid.NewGuid().ToString("N"));

    private IDalamudPluginInterface PluginInterface(
        Func<object?> read, bool writeFile)
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

    private string[] Backups() =>
        Directory.GetFiles(_directory, "Poser.json.bak-*");

    [Fact]
    public void AnUnreadableConfigIsBackedUpAndReported()
    {
        var service = new ConfigurationService(
            PluginInterface(() => throw new InvalidOperationException("bad json"), true));

        Assert.NotEmpty(service.LoadFailure);
        Assert.Contains("bad json", service.LoadFailure, StringComparison.Ordinal);
        Assert.Single(Backups());
    }

    [Fact]
    public void AConfigOfTheWrongShapeIsBackedUpToo()
    {
        // A null read is the same loss as a throwing one: the file is there,
        // it did not become a PoserConfiguration, and defaults are about to
        // overwrite it.
        var service = new ConfigurationService(
            PluginInterface(() => null, true));

        Assert.NotEmpty(service.LoadFailure);
        Assert.Single(Backups());
    }

    [Fact]
    public void AFirstRunIsNotAFailure()
    {
        var service = new ConfigurationService(
            PluginInterface(() => null, writeFile: false));

        Assert.Empty(service.LoadFailure);
        Assert.Empty(Backups());
    }

    [Fact]
    public void AReadableConfigIsKeptWhole()
    {
        var stored = new PoserConfiguration { UndoDepth = 37 };
        var service = new ConfigurationService(
            PluginInterface(() => stored, writeFile: true));

        Assert.Empty(service.LoadFailure);
        Assert.Equal(37, service.Config.UndoDepth);
        Assert.Empty(Backups());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }
}
