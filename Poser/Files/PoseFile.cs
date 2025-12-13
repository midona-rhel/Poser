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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // Keep PascalCase to match Brio
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
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

/// <summary>
/// Converts between Anamnesis bone naming convention and game bone names.
/// </summary>
public static class AnamnesisBoneNameConverter
{
    private static readonly Dictionary<string, string> AnamnesisToGameMap = new()
    {
        // Face bones
        { "Head", "j_kao" },
        { "Nose", "j_hana" },
        { "Jaw", "j_ago" },
        { "EyelidLowerLeft", "j_f_mayu_l" },
        { "EyelidLowerRight", "j_f_mayu_r" },

        // Body bones
        { "Root", "n_root" },
        { "Abdomen", "j_kosi" },
        { "Throw", "n_throw" },
        { "Waist", "j_sebo_a" },
        { "SpineA", "j_sebo_a" },
        { "SpineB", "j_sebo_b" },
        { "SpineC", "j_sebo_c" },
        { "Neck", "j_kubi" },

        // Arms
        { "ClavicleLeft", "j_sako_l" },
        { "ClavicleRight", "j_sako_r" },
        { "ArmLeft", "j_ude_a_l" },
        { "ArmRight", "j_ude_a_r" },
        { "ForearmLeft", "j_ude_b_l" },
        { "ForearmRight", "j_ude_b_r" },
        { "HandLeft", "j_te_l" },
        { "HandRight", "j_te_r" },

        // Legs
        { "LegLeft", "j_asi_a_l" },
        { "LegRight", "j_asi_a_r" },
        { "KneeLeft", "j_asi_b_l" },
        { "KneeRight", "j_asi_b_r" },
        { "CalfLeft", "j_asi_c_l" },
        { "CalfRight", "j_asi_c_r" },
        { "FootLeft", "j_asi_d_l" },
        { "FootRight", "j_asi_d_r" },
        { "ToesLeft", "j_asi_e_l" },
        { "ToesRight", "j_asi_e_r" },
    };

    private static readonly Dictionary<string, string> GameToAnamnesisMap;

    static AnamnesisBoneNameConverter()
    {
        GameToAnamnesisMap = new Dictionary<string, string>();
        foreach (var kvp in AnamnesisToGameMap)
        {
            GameToAnamnesisMap[kvp.Value] = kvp.Key;
        }
    }

    /// <summary>
    /// Converts an Anamnesis bone name to the game's internal name.
    /// Returns the original name if no mapping exists.
    /// </summary>
    public static string ToGame(string anamnesisName)
    {
        return AnamnesisToGameMap.TryGetValue(anamnesisName, out var gameName)
            ? gameName
            : anamnesisName;
    }

    /// <summary>
    /// Converts a game bone name to Anamnesis format.
    /// Returns the original name if no mapping exists.
    /// </summary>
    public static string ToAnamnesis(string gameName)
    {
        return GameToAnamnesisMap.TryGetValue(gameName, out var anamnesisName)
            ? anamnesisName
            : gameName;
    }
}
