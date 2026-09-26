namespace Poser.Config;

/// <summary>The host locates storage; settings behavior owns migrations and notifications.</summary>
public interface IConfigurationPersistence
{
    ConfigurationLoadResult Load();
    void Save(PoserConfiguration configuration);
}
