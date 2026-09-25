using Dalamud.Plugin;
using Poser.Config;

namespace Poser.Composition;

internal sealed class HostConfigurationPersistence(IDalamudPluginInterface plugin) : IConfigurationPersistence
{
    private readonly ConfigurationFileStore _store = new(plugin.ConfigFile.FullName);
    public ConfigurationLoadResult Load() => _store.Load();
    public void Save(PoserConfiguration configuration) => _store.Save(configuration);
}
