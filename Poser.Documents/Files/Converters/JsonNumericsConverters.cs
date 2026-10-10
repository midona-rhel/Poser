using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poser.Files.Converters;

// Brio (and Anamnesis before it) serialize numerics as comma-space separated strings,
// e.g. "Position": "0.1, 1, -0.05". These converters replicate that wire format exactly
// (see Brio/Brio/Files/Converters/VectorConverters.cs + QuaternionConverter.cs) —
// without them, System.Text.Json writes Vector3/Quaternion (public fields, no properties)
// as "{}" and fails to read real Brio/Anamnesis pose files.

public class Vector2Converter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = ReadParts(ref reader, 2, nameof(Vector2));
        return new Vector2(parts[0], parts[1]);
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        RequireFinite(nameof(Vector2), value.X, value.Y);
        writer.WriteStringValue(FormattableString.Invariant($"{value.X}, {value.Y}"));
    }

    internal static float[] ReadParts(ref Utf8JsonReader reader, int count, string typeName)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"{typeName} must be a string.");
        // Bound the raw token before decoding it: a component is a float, so
        // anything longer than every component at its cap is not a vector.
        var limit = count * (MaxComponentCharacters + 2);
        if ((reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > limit * 6L)
            throw new JsonException($"{typeName} exceeds {limit} characters.");
        return ParseComponents(reader.GetString()!, count, typeName);
    }

    /// <summary>The longest single component accepted; a float needs far
    /// fewer characters even fully spelled out.</summary>
    internal const int MaxComponentCharacters = 64;

    /// <summary>
    /// Parses the Anamnesis/Brio wire string: components separated by
    /// comma-space, each a culture-invariant float with '.' as the only
    /// decimal separator. A comma INSIDE a component ("0,5") is a document
    /// written under a decimal-comma culture; it is refused by name rather
    /// than guessed at, because reading it either way can be wrong.
    /// </summary>
    internal static float[] ParseComponents(string text, int count, string typeName)
    {
        if (text.Length > count * (MaxComponentCharacters + 2))
            throw new JsonException(
                $"{typeName} exceeds {count * (MaxComponentCharacters + 2)} characters.");

        var parts = text.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != count)
            throw new JsonException($"Expected {count} components for {typeName}, got {parts.Length}.");

        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            var part = parts[i];
            if (part.Contains(','))
                throw new JsonException(
                    $"{typeName} component '{part}' uses a decimal comma; " +
                    "pose numbers must use '.' as the decimal separator.");
            if (part.Length > MaxComponentCharacters ||
                !float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !float.IsFinite(value))
                throw new JsonException($"{typeName} contains an invalid numeric value '{part}'.");
            values[i] = value;
        }
        return values;
    }

    internal static void RequireFinite(string typeName, params float[] values)
    {
        foreach (var value in values)
            if (!float.IsFinite(value))
                throw new JsonException($"{typeName} contains NaN or infinity.");
    }
}

public class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = Vector2Converter.ReadParts(ref reader, 3, nameof(Vector3));
        return new Vector3(parts[0], parts[1], parts[2]);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        Vector2Converter.RequireFinite(nameof(Vector3), value.X, value.Y, value.Z);
        writer.WriteStringValue(FormattableString.Invariant($"{value.X}, {value.Y}, {value.Z}"));
    }
}

public class Vector4Converter : JsonConverter<Vector4>
{
    public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = Vector2Converter.ReadParts(ref reader, 4, nameof(Vector4));
        return new Vector4(parts[0], parts[1], parts[2], parts[3]);
    }

    public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
    {
        Vector2Converter.RequireFinite(nameof(Vector4), value.X, value.Y, value.Z, value.W);
        writer.WriteStringValue(FormattableString.Invariant($"{value.X}, {value.Y}, {value.Z}, {value.W}"));
    }
}

public class QuaternionConverter : JsonConverter<Quaternion>
{
    public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = Vector2Converter.ReadParts(ref reader, 4, nameof(Quaternion));
        return new Quaternion(parts[0], parts[1], parts[2], parts[3]);
    }

    public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
    {
        Vector2Converter.RequireFinite(nameof(Quaternion), value.X, value.Y, value.Z, value.W);
        writer.WriteStringValue(FormattableString.Invariant($"{value.X}, {value.Y}, {value.Z}, {value.W}"));
    }
}
