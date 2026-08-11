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

    /// <summary>Names seen for the first time and not yet reported. A modded
    /// character carries hundreds of bones outside the translation tables, and
    /// every one of them used to cost a SEPARATE synchronous log write on the
    /// framework tick that rebuilt the skeleton — the dominant term in the
    /// 50-63 ms redraw hitch. The dedup set is unchanged (each name is still
    /// reported exactly once per session); only the delivery is coalesced into
    /// one line by <see cref="FlushUntranslatedLog"/>.</summary>
    private static readonly List<string> _pendingUntranslated = new();

    /// <summary>How many names one flushed line spells out before it
    /// summarises the remainder as a count.</summary>
    private const int MaxListedUntranslated = 40;

    private static readonly Dictionary<string, BoneData> _boneData = new();
    private static readonly HashSet<string> _nsfwBones = new();

    /// <summary>
    /// Initializes the bone info service with a logger.
    /// </summary>
    public static void Initialize(IPluginLog log)
    {
        _log = log;
        lock (_loggedUntranslated)
        {
            _loggedUntranslated.Clear();
            _pendingUntranslated.Clear();
        }
        _boneData.Clear();
        _nsfwBones.Clear();

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
        // IVCS and physics registrations double as the NSFW set: the
        // Display.ShowNsfwBones switch ("Show IVCS and extended bone
        // groups") controls exactly the bones these two files declare.
        var extended = new Dictionary<string, BoneData>();
        IVCSBones.Register(extended);
        PhysicsBones.Register(extended);
        foreach (var (name, boneData) in extended)
        {
            _boneData[name] = boneData;
            _nsfwBones.Add(name);
        }

        log.Info($"[BoneInfoService] Loaded {_boneData.Count} bone definitions");
    }

    /// <summary>True for IVCS/extended-group bones — the set the
    /// Display.ShowNsfwBones switch shows and hides.</summary>
    public static bool IsNsfw(string boneName) => _nsfwBones.Contains(boneName);

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
    /// Gets the display name for a bone: the translation, or the internal name
    /// when untranslated. The raw bone name is NOT appended — surfaces that
    /// want it show a separate mono badge ("Jaw (j_f_ago)" next to a j_f_ago
    /// badge was the round-4 "name followed by name" defect).
    /// Logs untranslated bones once per session.
    /// </summary>
    public static string GetDisplayName(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
        {
            return data.Translation;
        }

        // Record the untranslated bone (only once per bone name). Multiple
        // threads resolve display names (framework refreshes and the UI), and
        // an unsynchronized HashSet corrupts permanently under concurrent Add.
        // NOTHING is written to the log here: a skeleton rebuild resolves every
        // bone name in one framework tick, so per-name writes turned a redraw
        // into hundreds of synchronous log writes. FlushUntranslatedLog emits
        // them as one line.
        lock (_loggedUntranslated)
        {
            if (_loggedUntranslated.Add(boneName))
                _pendingUntranslated.Add(boneName);
        }

        return boneName;
    }

    /// <summary>
    /// Emits ONE warning listing every untranslated bone name seen since the
    /// previous flush, and nothing at all when there is none — the common case,
    /// so it is safe to call on every scene refresh. Each name still appears
    /// exactly once per session; only the number of log writes changes.
    /// </summary>
    public static void FlushUntranslatedLog()
    {
        if (_log == null)
            return;

        string[] names;
        lock (_loggedUntranslated)
        {
            if (_pendingUntranslated.Count == 0)
                return;
            names = _pendingUntranslated.ToArray();
            _pendingUntranslated.Clear();
        }

        var listed = names.Length <= MaxListedUntranslated
            ? names
            : names[..MaxListedUntranslated];
        var overflow = names.Length - listed.Length;
        var suffix = overflow > 0 ? $" (+{overflow} more)" : string.Empty;
        _log.Warning(
            $"[BoneInfo] {names.Length} untranslated bone(s): {string.Join(", ", listed)}{suffix}");
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

    /// <summary>
    /// Gets the root bone name for a category (the actual bone that represents the category).
    /// Returns null for abstract categories like Equipment and Other.
    /// </summary>
    public static string? GetCategoryRootBone(BoneCategory category) => category switch
    {
        BoneCategory.Root => "n_root",
        BoneCategory.Spine => "j_kosi",
        BoneCategory.Head => "j_kao",
        BoneCategory.LeftArm => "j_ude_a_l",
        BoneCategory.RightArm => "j_ude_a_r",
        BoneCategory.LeftLeg => "j_asi_a_l",
        BoneCategory.RightLeg => "j_asi_a_r",
        BoneCategory.Tail => "n_sippo_a",
        _ => null  // Equipment and Other stay abstract
    };

    /// <summary>
    /// Gets the root bone name for a subcategory (the actual bone that represents the subcategory).
    /// Returns null for abstract subcategories or those without a clear root bone.
    /// </summary>
    public static string? GetSubcategoryRootBone(BoneSubcategory subcategory) => subcategory switch
    {
        BoneSubcategory.Hair => "j_kami_a",
        BoneSubcategory.Ears => "j_mimi_l",
        BoneSubcategory.LeftEye => "j_f_eye_l",
        BoneSubcategory.RightEye => "j_f_eye_r",
        BoneSubcategory.Mouth => "j_ago",
        _ => null  // Most subcategories stay abstract
    };
}
