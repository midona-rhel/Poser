using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poser.Files;

/// <summary>
/// Brio-compatible pose file format (.pose).
/// Matches Brio's JSON structure exactly for full compatibility.
/// </summary>
[Serializable]
public class PoseFile
{
    public string TypeName { get; set; } = "Brio Pose";

    // Metadata (optional)
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public string? Base64Image { get; set; }
    public List<string>? Tags { get; set; }

    public BoneData ModelDifference { get; set; } = BoneData.Identity;
    public BoneData ModelAbsoluteValues { get; set; } = BoneData.Identity;

    public Dictionary<string, BoneData> Bones { get; set; } = new();
    public Dictionary<string, BoneData> MainHand { get; set; } = new();
    public Dictionary<string, BoneData> OffHand { get; set; } = new();
    public Dictionary<string, BoneData> Prop { get; set; } = new();
    public Dictionary<string, BoneData> Ornament { get; set; } = new();

    // Legacy fields for compatibility with other pose tools
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    /// <summary>
    /// Bone transform data matching Brio's format.
    /// </summary>
    [Serializable]
    public class BoneData
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public static BoneData Identity => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        public static implicit operator Transform(BoneData bone)
        {
            return new Transform(bone.Position, bone.Rotation, bone.Scale);
        }

        public static implicit operator BoneData(Transform transform)
        {
            return new BoneData
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale
            };
        }
    }

    // Mirrors Brio/Brio/Core/JsonSerializer.cs so files round-trip byte-compatibly:
    // numerics as "X, Y, Z" strings, relaxed escaping (smaller Base64Image), trailing commas tolerated.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = null, // Keep PascalCase to match Brio
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
    /// Loads a pose file from disk.
    /// </summary>
    public static PoseFile? Load(string path)
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
    /// Parses a pose file from JSON string.
    /// </summary>
    public static PoseFile? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PoseFile>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves this pose file to disk.
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

    /// <summary>
    /// Converts Anamnesis bone names to game bone names.
    /// Required for compatibility with old Anamnesis pose files.
    /// </summary>
    public void SanitizeBoneNames()
    {
        var newBones = new Dictionary<string, BoneData>();
        foreach (var bone in Bones)
        {
            newBones[AnamnesisBoneNameConverter.ToGame(bone.Key)] = bone.Value;
        }
        Bones = newBones;
    }
}
