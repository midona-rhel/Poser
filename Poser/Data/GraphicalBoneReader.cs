using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Poser.Data.Config;

namespace Poser.Data;

/// <summary>
/// Reads graphical bone position configuration from JSON.
/// </summary>
public static class GraphicalBoneReader
{
    private const string ResourceName = "Poser.Data.GraphicalBones.GraphicalBonePosePositions.json";

    private static GraphicalBoneConfig? _cachedConfig;

    /// <summary>
    /// Reads graphical bone configuration from embedded resource.
    /// Result is cached for subsequent calls.
    /// </summary>
    public static GraphicalBoneConfig ReadEmbeddedResource()
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        var assembly = typeof(GraphicalBoneReader).Assembly;

        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Could not find embedded resource: {ResourceName}");
        }

        _cachedConfig = ReadStream(stream);
        return _cachedConfig;
    }

    /// <summary>
    /// Reads graphical bone configuration from a stream.
    /// </summary>
    public static GraphicalBoneConfig ReadStream(Stream stream)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var config = JsonSerializer.Deserialize<GraphicalBoneConfig>(stream, options)
            ?? throw new InvalidOperationException("Failed to deserialize graphical bone config");

        // Parse position strings into Vector2
        foreach (var section in config.PoseImages.Values)
        {
            foreach (var bone in section.Bones)
            {
                bone.PositionVector = ParsePosition(bone.Position);
            }
        }

        // Process parent references
        config.ProcessParentReferences();

        return config;
    }

    private static Vector2 ParsePosition(string positionStr)
    {
        if (string.IsNullOrEmpty(positionStr))
            return Vector2.Zero;

        var parts = positionStr.Split(',');
        if (parts.Length != 2)
            return Vector2.Zero;

        if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return new Vector2(x, y);
        }

        return Vector2.Zero;
    }

    /// <summary>
    /// Gets the embedded image bytes for a graphical bone image.
    /// </summary>
    /// <param name="imageName">Image name without extension (e.g., "PoseBody")</param>
    /// <returns>Image bytes or null if not found</returns>
    public static byte[]? GetImageBytes(string imageName)
    {
        var assembly = typeof(GraphicalBoneReader).Assembly;
        var resourceName = $"Poser.Data.GraphicalBones.Images.{imageName}.png";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
