using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Files;

/// <summary>
/// Poser light file format (.xivl). Carries EVERY property an
/// <see cref="ILight"/> owns, including the absolute transform and the flag
/// set — Ktisis' .ktlight and Brio's light DTO each drop part of that, and a
/// light that comes back missing its shadow flags or its falloff type is not
/// the light that was saved.
/// </summary>
[Serializable]
public class LightFile
{
    /// <summary>Bumped on any breaking meaning change of a persisted field.
    /// Version 1 fixed AreaAngle to degrees end-to-end; version 0 files
    /// (no field) predate the unit fix and carry the same numbers with
    /// undefined skew semantics — they load as-is.</summary>
    public const int CurrentVersion = 1;

    public string TypeName { get; set; } = "Poser Light";
    public int FileVersion { get; set; } = CurrentVersion;

    public string Name { get; set; } = "Light";
    public LightKind Kind { get; set; }
    public bool IsOn { get; set; } = true;

    public TransformData Transform { get; set; } = TransformData.Identity;

    /// <summary>Where the camera stood at save, for
    /// <see cref="ObjectPlacementMode.RelativeToCamera"/> loads. Absent in a
    /// file saved before anchors existed; a relative load then refuses by
    /// name rather than guessing.</summary>
    public PlacementAnchorData? CameraAnchor { get; set; }

    /// <summary>Where the selected actor stood at save, for
    /// <see cref="ObjectPlacementMode.RelativeToSelectedActor"/> loads.
    /// Absent when nothing was selected at save.</summary>
    public PlacementAnchorData? ActorAnchor { get; set; }

    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public float Range { get; set; }
    public float Falloff { get; set; }
    public LightFalloffType FalloffType { get; set; }
    public float SpotAngle { get; set; }
    public float FalloffAngle { get; set; }
    public Vector2 AreaAngle { get; set; }

    public bool HasReflection { get; set; }
    public bool CastsDynamicShadows { get; set; }
    public bool CastsCharacterShadow { get; set; }
    public bool CastsObjectShadow { get; set; }
    public float CharacterShadowRange { get; set; }
    public float ShadowPlaneNear { get; set; }
    public float ShadowPlaneFar { get; set; }

    /// <summary>Game path of the projected gobo texture, null when the light
    /// has none. Ktisis' .ktlight v2 field, stored by path so a library that
    /// renames an entry still resolves it.</summary>
    public string? Gobo { get; set; }

    /// <summary>
    /// The light's world transform. Absolute, unlike a pose file's bone
    /// data — a light has no rest pose to take a difference against.
    /// </summary>
    [Serializable]
    public class TransformData
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public static TransformData Identity => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One
        };

        public static implicit operator Transform(TransformData data)
        {
            return new Transform(data.Position, data.Rotation, data.Scale);
        }

        public static implicit operator TransformData(Transform transform)
        {
            return new TransformData
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale
            };
        }
    }

    // The same wire style .pose files use — numerics as "X, Y, Z" strings,
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

    /// <summary>
    /// Loads a light file from disk.
    /// </summary>
    public static LightFile? Load(string path)
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

    /// <summary>
    /// Parses a light file from JSON string.
    /// </summary>
    public static LightFile? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LightFile>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves this light file to disk.
    /// </summary>
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
