using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Poser writes a plain string array; Brio's <c>TagCollection</c> writes
    /// an array of tag OBJECTS. The converter reads both so a Brio-authored
    /// .pose or clipboard payload that carries tags still loads — without it
    /// the shape mismatch rejects the entire document, not just its tags
    /// (<see cref="Converters.TagListConverter"/>).
    /// </summary>
    [JsonConverter(typeof(Converters.TagListConverter))]
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
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        MaxDepth = PoseFileLimits.MaxJsonDepth,
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
    /// Lossy compatibility load. Returns null for every typed read, size,
    /// JSON, or validation failure; new storage workflows use
    /// <see cref="AtomicPoseFileStore.Read"/>.
    /// </summary>
    public static PoseFile? Load(string path)
    {
        // Intentionally lossy compatibility wrapper. New storage callers use
        // AtomicPoseFileStore.Read so the typed failure and path survive.
        return AtomicPoseFileStore.Default.Read(path).Pose;
    }

    /// <summary>
    /// Lossy compatibility parse. Returns null for every typed size, JSON, or
    /// validation failure; new storage workflows use
    /// <see cref="AtomicPoseFileStore.Parse"/>.
    /// </summary>
    public static PoseFile? FromJson(string json)
    {
        // Intentionally lossy compatibility wrapper for clipboard/rest-pose
        // callers that predate the typed ordinary-pose codec outcome.
        return AtomicPoseFileStore.Default.Parse(json).Pose;
    }

    /// <summary>
    /// Lossy compatibility save. Returns false for every typed validation,
    /// serialization, temp, flush, validation, replace, or move failure; new
    /// storage workflows use <see cref="AtomicPoseFileStore.Write"/>.
    /// </summary>
    public bool Save(string path)
    {
        // Intentionally lossy compatibility wrapper. The typed store retains
        // the phase and any undeletable temp as recovery evidence.
        return AtomicPoseFileStore.Default.Write(this, path).Succeeded;
    }

    /// <summary>
    /// Converts Anamnesis bone names to game bone names.
    /// Required for compatibility with old Anamnesis pose files.
    /// </summary>
    public void SanitizeBoneNames()
    {
        var aliases = PoseFileValidation.ValidateAnamnesisAliases(Bones);
        if (!aliases.Succeeded)
            throw new InvalidDataException(aliases.Failure!.Detail);

        var newBones = new Dictionary<string, BoneData>();
        foreach (var bone in Bones.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            newBones[AnamnesisBoneNameConverter.ToGame(bone.Key)] = bone.Value;
        }
        Bones = newBones;
    }
}
