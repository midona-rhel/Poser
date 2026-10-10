using Poser.Documents.Config;
using Poser.Application.Settings;

namespace Poser.Tests.Fixtures;

internal sealed class MemoryConfigurationPersistence : IConfigurationPersistence
{
    public PoserConfiguration Configuration { get; private set; } = new();
    public ConfigurationLoadResult Load() => new(Configuration);
    public void Save(PoserConfiguration configuration) => Configuration = configuration;
}
