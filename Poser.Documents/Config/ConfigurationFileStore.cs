using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Poser.Documents.Files;

namespace Poser.Documents.Config;

/// <summary>Configuration JSON and recovery, independent of the plugin host.</summary>
public sealed class ConfigurationFileStore(string path)
{
    /// <summary>
    /// How the settings file is read and written. Files written before the
    /// move to System.Text.Json came from Newtonsoft, so this reads the way
    /// Newtonsoft did: names in any letter case, public fields, numbers in
    /// quotes, and filling the objects and collections a new configuration
    /// starts with (so Library.Favorites keeps its ignore-case comparer).
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
            | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        WriteIndented = true,
        // Writes "Ctrl+Z" and accented folder names as they are, not as
        // escape codes, as the Newtonsoft writer did. Only Poser reads this
        // file; it never goes into a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = SettingsResolver(),
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ConfigurationLoadResult Load()
    {
        if (!File.Exists(path)) return new(new PoserConfiguration());
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is not JsonObject document
                || document["Version"] is null)
                throw new JsonException("The file is not a Poser configuration.");
            RemoveTypeMetadata(document);
            var config = document.Deserialize<PoserConfiguration>(JsonOptions)
                ?? throw new JsonException("The configuration was empty.");
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
        AtomicFile.Write(new SystemAtomicFileSystem(), destination, stream =>
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JsonOptions.Encoder });
            JsonSerializer.Serialize(writer, configuration, JsonOptions);
        }, new AtomicWriteOptions { Subject = "configuration" }).ThrowIfFailed();
    }

    /// <summary>
    /// Older settings files carry Newtonsoft's type names: "$type" on every
    /// object, and lists wrapped as {"$type": …, "$values": […]}. The types
    /// they name no longer exist, so the names are dropped and the lists
    /// unwrapped before reading.
    /// </summary>
    private static JsonNode? RemoveTypeMetadata(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject wrapper when wrapper["$values"] is JsonArray values:
                wrapper.Remove("$values");
                return RemoveTypeMetadata(values);
            case JsonObject obj:
                obj.Remove("$type");
                foreach (var name in obj.Select(property => property.Key).ToList())
                {
                    var child = obj[name];
                    var cleaned = RemoveTypeMetadata(child);
                    if (!ReferenceEquals(child, cleaned))
                        obj[name] = cleaned;
                }
                return obj;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    var cleaned = RemoveTypeMetadata(child);
                    if (!ReferenceEquals(child, cleaned))
                        array[i] = cleaned;
                }
                return array;
            default:
                return node;
        }
    }

    /// <summary>Poser's own resolver (see <see cref="PoserJsonContracts"/>).
    /// <see cref="UIConfiguration.Keybinds"/> is read from files older than
    /// version 3 and never written.</summary>
    private static DefaultJsonTypeInfoResolver SettingsResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static type =>
        {
            if (type.Type != typeof(UIConfiguration))
                return;
            foreach (var property in type.Properties)
                if (property.Name == nameof(UIConfiguration.Keybinds))
                    property.ShouldSerialize = static (_, _) => false;
        });
        return resolver;
    }
}
