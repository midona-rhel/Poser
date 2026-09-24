using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poser.Files.Converters;

/// <summary>
/// Reads either tag shape into Poser's plain string list.
///
/// <para>CURRENT Brio agrees with Poser on the wire. Its <c>Tags</c> is a
/// <c>TagCollection</c> (Services/Library/Tags/TagCollection.cs), an
/// <c>ICollection&lt;Tag&gt;</c>, but <c>TagCollectionConverter</c> is
/// registered in BOTH of Brio's option sets (Core/JsonSerializer.cs:30, :44)
/// and it reads and writes a plain string array. The object shape
/// (<c>[{"DisplayName":"x","Name":"x","Aliases":[],"IsToolGenerated":false}]</c>)
/// is what a <c>TagCollection</c> serializes as WITHOUT that converter, which
/// is what older documents in the wild carry.</para>
///
/// <para>This converter exists for those older documents: without it, one tag
/// in the object shape fails to deserialize and the WHOLE pose is rejected,
/// not just its tags. Writing stays the plain string array, which is the shape
/// current Brio both writes and reads.</para>
/// </summary>
public sealed class TagListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Tags must be an array.");

        var tags = new List<string>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return tags;
                case JsonTokenType.String:
                    if (reader.GetString() is { } text)
                        tags.Add(text);
                    break;
                case JsonTokenType.StartObject:
                    // Brio's Tag: Name and DisplayName are the same string;
                    // Name is the one its own tag matching reads
                    // (FileUIHelpers.cs:428).
                    if (ReadTagObject(ref reader) is { } name)
                        tags.Add(name);
                    break;
                case JsonTokenType.Null:
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        throw new JsonException("Unterminated tag array.");
    }

    private static string? ReadTagObject(ref Utf8JsonReader reader)
    {
        string? name = null;
        string? displayName = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return name ?? displayName;
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;
            var property = reader.GetString();
            reader.Read();
            if (reader.TokenType == JsonTokenType.String)
            {
                if (string.Equals(property, "Name", StringComparison.OrdinalIgnoreCase))
                    name = reader.GetString();
                else if (string.Equals(property, "DisplayName", StringComparison.OrdinalIgnoreCase))
                    displayName = reader.GetString();
            }
            // A no-op on a value token, the whole subtree on a container.
            reader.Skip();
        }
        throw new JsonException("Unterminated tag object.");
    }

    public override void Write(
        Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var tag in value)
            writer.WriteStringValue(tag);
        writer.WriteEndArray();
    }
}
