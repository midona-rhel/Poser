using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Poser.Core.BoneInfo;

/// <summary>
/// Central service for bone information (translations and categories).
/// Aggregates data from all category files.
/// </summary>
public static class BoneInfoService
{
    private static IPluginLog? _log;
    private static readonly HashSet<string> _loggedUntranslated = new();
    private static readonly Dictionary<string, BoneData> _boneData = new();

    /// <summary>
    /// Initializes the bone info service with a logger.
    /// </summary>
    public static void Initialize(IPluginLog log)
    {
        _log = log;
        _loggedUntranslated.Clear();
        _boneData.Clear();

        // Register all category data
        RootBones.Register(_boneData);
        SpineBones.Register(_boneData);
        ChestBones.Register(_boneData);
        HeadBones.Register(_boneData);
        FaceBones.Register(_boneData);
        HairBones.Register(_boneData);
        EarBones.Register(_boneData);
        LeftArmBones.Register(_boneData);
        RightArmBones.Register(_boneData);
        LeftHandBones.Register(_boneData);
        RightHandBones.Register(_boneData);
        LeftLegBones.Register(_boneData);
        RightLegBones.Register(_boneData);
        TailBones.Register(_boneData);
        ClothingBones.Register(_boneData);
        EquipmentBones.Register(_boneData);
        IVCSBones.Register(_boneData);
        PhysicsBones.Register(_boneData);

        log.Info($"[BoneInfoService] Loaded {_boneData.Count} bone definitions");
    }

    /// <summary>
    /// Gets the translated name for a bone, or null if no translation exists.
    /// </summary>
    public static string? GetTranslation(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
            return data.Translation;
        return null;
    }

    /// <summary>
    /// Gets the category for a bone.
    /// </summary>
    public static BoneCategory GetCategory(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
            return data.Category;
        return BoneCategory.Other;
    }

    /// <summary>
    /// Gets both translation and category for a bone.
    /// </summary>
    public static BoneData? GetBoneData(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
            return data;
        return null;
    }

    /// <summary>
    /// Gets the display name for a bone: "Translation (internal_name)" or just "internal_name" if no translation.
    /// Logs untranslated bones once per session.
    /// </summary>
    public static string GetDisplayName(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
        {
            return $"{data.Translation} ({boneName})";
        }

        // Log untranslated bone (only once per bone name)
        if (_log != null && _loggedUntranslated.Add(boneName))
        {
            _log.Warning($"[BoneInfo] Untranslated bone: {boneName}");
        }

        return boneName;
    }

    /// <summary>
    /// Checks if bone data exists for the given bone name.
    /// </summary>
    public static bool HasBoneData(string boneName)
    {
        return _boneData.ContainsKey(boneName);
    }

    /// <summary>
    /// Gets the display name for a category.
    /// </summary>
    public static string GetCategoryDisplayName(BoneCategory category)
    {
        return category switch
        {
            BoneCategory.Root => "Root",
            BoneCategory.Head => "Head",
            BoneCategory.Spine => "Spine",
            BoneCategory.LeftArm => "Left Arm",
            BoneCategory.RightArm => "Right Arm",
            BoneCategory.LeftLeg => "Left Leg",
            BoneCategory.RightLeg => "Right Leg",
            BoneCategory.Tail => "Tail",
            BoneCategory.Equipment => "Equipment",
            BoneCategory.Other => "Other",
            _ => category.ToString()
        };
    }

    /// <summary>
    /// Gets the display name for a subcategory.
    /// </summary>
    public static string GetSubcategoryDisplayName(BoneSubcategory subcategory)
    {
        return subcategory switch
        {
            BoneSubcategory.None => "",
            BoneSubcategory.Face => "Face",
            BoneSubcategory.LeftEye => "Left Eye",
            BoneSubcategory.RightEye => "Right Eye",
            BoneSubcategory.Eyebrows => "Eyebrows",
            BoneSubcategory.Nose => "Nose",
            BoneSubcategory.Mouth => "Mouth",
            BoneSubcategory.Cheeks => "Cheeks",
            BoneSubcategory.Hair => "Hair",
            BoneSubcategory.Ears => "Ears",
            BoneSubcategory.Hand => "Hand",
            BoneSubcategory.Fingers => "Fingers",
            BoneSubcategory.Foot => "Foot",
            BoneSubcategory.Toes => "Toes",
            _ => subcategory.ToString()
        };
    }

    /// <summary>
    /// Gets the subcategory for a bone.
    /// </summary>
    public static BoneSubcategory GetSubcategory(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
            return data.Subcategory;
        return BoneSubcategory.None;
    }
}
