using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Poser.Config;

/// <summary>Configuration JSON and recovery, independent of the plugin host.</summary>
public sealed class ConfigurationFileStore(string path)
{
    // The schema is known. Old $type metadata names Poser.Core (including
    // generic arguments); ignore it instead of resolving arbitrary CLR types.
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        Formatting = Formatting.Indented,
    };

    public ConfigurationLoadResult Load()
    {
        if (!File.Exists(path)) return new(new PoserConfiguration());
        try
        {
            var document = JObject.Parse(File.ReadAllText(path));
            if (document["Version"] is null)
                throw new JsonSerializationException("The file is not a Poser configuration.");
            var config = document.ToObject<PoserConfiguration>(JsonSerializer.Create(Settings))
                ?? throw new JsonSerializationException("The configuration was empty.");
            return new(config);
        }
        catch (Exception ex)
        {
            var detail = $"Your settings could not be read and have been reset to defaults ({ex.Message}).";
            try
            {
                var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Copy(path, backup, overwrite: true);
                return new(new PoserConfiguration(), detail + $" The old file was saved as {backup}.");
            }
            catch (Exception backupError)
            {
                return new(new PoserConfiguration(), detail + " Backing the old file up also failed: " + backupError.Message);
            }
        }
    }

    public void Save(PoserConfiguration configuration)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    JsonSerializer.Create(Settings).Serialize(writer, configuration);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
