using System;
using System.Globalization;
using System.Numerics;
using System.Text;
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
        var str = reader.GetString() ?? throw new JsonException($"Cannot convert null to {typeName}");
        var parser = new NumericComponentsParser(count, typeName);
        foreach (var character in str)
            parser.Append(character);
        return parser.Complete();
    }

    internal static void RequireFinite(string typeName, params float[] values)
    {
        foreach (var value in values)
            if (!float.IsFinite(value))
                throw new JsonException($"{typeName} contains NaN or infinity.");
    }
}

// Both the normal JsonConverter path and the streaming metadata projection
// feed decoded string characters through this parser. It retains only the
// current component and caps the complete wire string at the pose file limit.
internal sealed class NumericComponentsParser
{
    internal const int MaxCharacters = (int)PoseFileLimits.MaxFileBytes;

    private readonly int _count;
    private readonly string _typeName;
    private readonly StringBuilder _component = new();
    private readonly float[] _values;
    private int _componentCount;
    private int _characterCount;
    private bool _pendingComma;

    internal NumericComponentsParser(int count, string typeName)
    {
        _count = count;
        _typeName = typeName;
        _values = new float[count];
    }

    internal void Append(char character)
    {
        if (++_characterCount > MaxCharacters)
            throw new FormatException(
                $"{_typeName} exceeds {MaxCharacters} numeric string characters.");

        if (_pendingComma)
        {
            _pendingComma = false;
            if (character == ' ')
            {
                ParseComponent();
                return;
            }

            _component.Append(',');
        }

        if (character == ',')
            _pendingComma = true;
        else
            _component.Append(character);
    }

    internal float[] Complete()
    {
        if (_pendingComma)
            _component.Append(',');
        ParseComponent();
        if (_componentCount != _count)
            throw new FormatException(
                $"Expected {_count} components for {_typeName}, got {_componentCount}.");
        return _values;
    }

    private void ParseComponent()
    {
        if (_component.Length == 0)
            return;
        if (_componentCount >= _count)
        {
            _componentCount++;
            _component.Clear();
            return;
        }

        if (!float.TryParse(
                _component.ToString(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var value) ||
            !float.IsFinite(value))
        {
            throw new JsonException($"{_typeName} contains an invalid numeric value.");
        }

        _values[_componentCount++] = value;
        _component.Clear();
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
