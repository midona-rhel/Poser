using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Domain.Scene;

namespace Poser.Files;

/// <summary>
/// Poser camera file format (.posercam). Carries EVERY property an
/// <see cref="Poser.Entities.IVirtualCamera"/> owns except the live flag and
/// the tracked bones — liveness belongs to the session and bone references
/// belong to the scene they were picked in. Angular values are stored in the
/// native radians the entity itself carries.
/// </summary>
[Serializable]
public class CameraFile
{
    public const int CurrentVersion = 1;

    public string TypeName { get; set; } = "Poser Camera";
    public int FileVersion { get; set; } = CurrentVersion;

    public string Name { get; set; } = "Camera";
    public CameraKind Kind { get; set; }

    // Orbit state.
    public Vector2 Angle { get; set; }
    public Vector2 Pan { get; set; }
    public float Roll { get; set; }
    public float Zoom { get; set; } = 2.5f;
    public float FoV { get; set; }
    public Vector3 PositionOffset { get; set; }

    /// <summary>The world point the camera is pinned to, or null when it is
    /// free to follow the game's update (Ktisis carries the same field in its
    /// scene file). Null and "0, 0, 0" are different answers here, which is
    /// why it is nullable rather than a sentinel.</summary>
    public Vector3? FixedPosition { get; set; }

    public bool DisableCollision { get; set; }
    public bool DelimitCamera { get; set; }

    // Free-cam state.
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public bool MovementEnabled { get; set; } = true;
    public bool Move2D { get; set; }
    public float MovementSpeed { get; set; } = 0.03f;
    public float MouseSensitivity { get; set; } = 0.1f;
    public bool DelimitAngle { get; set; }

    // Projection.
    public bool Orthographic { get; set; }
    public float OrthographicZoom { get; set; } = 10f;

    // The same wire style .poserlight uses — numerics as "X, Y, Z" strings,
    // enums by name, relaxed escaping, trailing commas tolerated.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
            new Converters.Vector2Converter(),
            new Converters.Vector3Converter(),
            new Converters.Vector4Converter(),
            new Converters.QuaternionConverter()
        }
    };

    public static CameraFile? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return FromJson(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static CameraFile? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CameraFile>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Save(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
