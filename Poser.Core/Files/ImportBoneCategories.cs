using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Poser.Files;

/// <summary>One row of the bone-filter menu: a named set of bone-name
/// prefixes (Brio BoneCategories.json, Filter entries).</summary>
public sealed record ImportBoneCategory(string Id, string Name, string[] Prefixes);

/// <summary>One group header of the bone-filter menu (Brio's Category
/// entries): Head Bones, Body Bones, IVCS Bones, Other Bones.</summary>
public sealed record ImportBoneCategoryGroup(string Name, ImportBoneCategory[] Categories);

/// <summary>
/// Brio's bone-filter catalog, read from its own
/// Resources/Embedded/Data/BoneCategories.json shipped byte-identical as
/// <c>Data/BoneCategories/BoneCategories.json</c> — the same treatment the
/// embedded rest poses get (<see cref="RestPoses"/>), so the two plugins
/// agree bone-for-bone on what every category claims instead of on a
/// hand-compressed approximation of it.
///
/// <para>Matching is Brio's: every JSON entry is a bone-name PREFIX
/// (BoneFilter.cs:127 <c>bone.Name.StartsWith(bonePrefix)</c>), a bone may
/// belong to several categories at once, and a bone NO category claims falls
/// to the "Other" row (BoneFilter's <c>_otherAllowed</c>). The "prop" and
/// "ornament" rows carry no prefixes in Brio either — they gate whole slots
/// and map onto the ApplyProp/ApplyOrnament options — and "weapon" gates the
/// MainHand/OffHand slots as well as claiming its two Character prefixes.
/// The one comparison difference: Poser matches ordinal-ignore-case
/// throughout, where Brio's bare <c>StringsWith(string)</c> is
/// culture-sensitive and therefore case-SENSITIVE. That only shows up on the
/// <c>"J_f_eyeprm_01_"</c> entry, whose capital J means Brio's "ex" row never
/// actually claims the bone it names; case-insensitivity honours the entry's
/// evident intent.</para>
///
/// <para>Groups follow the JSON's declaration order exactly as Brio's filter
/// editor draws it (PosingEditorCommon.cs:120-152: a Category entry opens a
/// group, the Filter entries after it are its rows). Group DISPLAY names are
/// shortened from Brio's ("Head Bones" → "Head") for the narrow menu; the
/// row names are its en.json <c>bone_categories</c> strings verbatim.</para>
/// </summary>
public static class ImportBoneCategories
{
    private const string CatalogResource =
        "Poser.Data.BoneCategories.BoneCategories.json";

    /// <summary>Brio's en.json <c>bone_categories</c> block verbatim — the
    /// JSON carries ids only, and Brio resolves each through
    /// <c>Localize.Get($"bone_categories.{id}", id)</c>
    /// (BoneCategories.cs:18). An id with no entry displays as its id, the
    /// same fallback.</summary>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        // Group headers: shortened from Brio's "… Bones" for the menu width.
        ["headCategory"] = "Head",
        ["bodyCategory"] = "Body",
        ["ivcsCategory"] = "IVCS",
        ["otherCategory"] = "Other",
        ["body"] = "Body",
        ["head"] = "Head",
        ["hair"] = "Hair",
        ["face"] = "Face",
        ["eyes"] = "Eyes",
        ["lips"] = "Lips",
        ["jaw"] = "Jaw",
        ["legs"] = "Legs",
        ["tail"] = "Tail",
        ["ears"] = "Ears",
        // Brio's ids are swapped relative to the sides they contain; the
        // DISPLAY names follow its popup (handsLeft holds the _r bones).
        ["handsLeft"] = "Right Arm",
        ["handsRight"] = "Left Arm",
        ["ivcsHandsRight"] = "IVCS Right Arm",
        ["ivcsHandsLeft"] = "IVCS Left Arm",
        ["ivcsPenis"] = "IVCS Penis",
        ["ivcsVagina"] = "IVCS Vagina",
        ["ivcsAbdomen"] = "IVCS Breast & Abdomen",
        ["ivcsLegs"] = "IVCS Legs & Feet",
        ["ivcsButt"] = "IVCS Butt",
        ["clothing"] = "Clothes",
        ["weapon"] = "Weapons",
        ["ex"] = "Dawntrail Other",
        ["legacy"] = "Legacy",
        ["other"] = "Other",
        ["prop"] = "Emote Props",
        ["ornament"] = "Fashion Accessories",
    };

    /// <summary>The category ids Brio's ExpressionOptions run enables
    /// (PosingService.cs:77-86: DisableAll, then head, ears, hair, face,
    /// eyes, lips and jaw back on).</summary>
    public static readonly string[] ExpressionCategories =
        { "head", "ears", "hair", "face", "eyes", "lips", "jaw" };

    /// <summary>The category ids Brio's BodyOptions run disables
    /// (PosingService.cs:65-75): weapons, the whole head group, legacy and
    /// ex.</summary>
    public static readonly string[] BodyOnlyExclusions =
        { "weapon", "ears", "hair", "face", "eyes", "lips", "jaw", "head", "legacy", "ex" };

    /// <summary>The category ids Brio's DefaultCMPImporterOptions run
    /// disables (PosingService.cs:50-59): the BodyOptions set minus legacy.
    /// </summary>
    public static readonly string[] CmpExclusions =
        { "weapon", "ears", "hair", "face", "eyes", "lips", "jaw", "head", "ex" };

    private static readonly Lazy<ImportBoneCategoryGroup[]> LoadedGroups =
        new(LoadGroups, isThreadSafe: true);

    public static ImportBoneCategoryGroup[] Groups => LoadedGroups.Value;

    private static readonly Lazy<ImportBoneCategory[]> FlatCategories =
        new(() => Groups.SelectMany(group => group.Categories).ToArray(),
            isThreadSafe: true);

    /// <summary>Every prefix-carrying category, flattened. Materialized once:
    /// the membership tests below run per BONE per import, and a fresh LINQ
    /// pipeline on each call is an allocation in that loop.</summary>
    public static IReadOnlyList<ImportBoneCategory> All => FlatCategories.Value;

    private static readonly JsonDocumentOptions CatalogOptions = new()
    {
        // Brio's shipped file carries trailing commas; its own resource
        // reader tolerates them and so must this one, byte-identical copy.
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Read through a <see cref="JsonDocument"/> rather than a
    /// Dictionary model: the file's DECLARATION ORDER is load-bearing (it is
    /// the menu's group/row order) and only the document preserves it by
    /// contract.</summary>
    private static ImportBoneCategoryGroup[] LoadGroups()
    {
        using var stream = typeof(ImportBoneCategories).Assembly
            .GetManifestResourceStream(CatalogResource)
            ?? throw new InvalidOperationException(
                $"Could not find embedded resource: {CatalogResource}");
        using var reader = new StreamReader(stream);
        using var document = JsonDocument.Parse(reader.ReadToEnd(), CatalogOptions);
        if (!document.RootElement.TryGetProperty("Categories", out var categories))
            throw new InvalidOperationException(
                $"Embedded bone catalog has no Categories: {CatalogResource}");

        var groups = new List<ImportBoneCategoryGroup>();
        // Declaration order IS the menu order (Brio PosingEditorCommon.cs:
        // 120-152): a Category entry opens a group and every Filter entry
        // after it is one of its rows, until the next Category.
        string? groupName = null;
        var rows = new List<ImportBoneCategory>();
        foreach (var entry in categories.EnumerateObject())
        {
            var id = entry.Name;
            var name = DisplayNames.TryGetValue(id, out var display) ? display : id;
            var type = entry.Value.TryGetProperty("Type", out var typeValue)
                ? typeValue.GetString()
                : null;
            if (string.Equals(type, "Category", StringComparison.OrdinalIgnoreCase))
            {
                if (groupName != null)
                    groups.Add(new ImportBoneCategoryGroup(groupName, rows.ToArray()));
                groupName = name;
                rows = new List<ImportBoneCategory>();
                continue;
            }

            var prefixes = new List<string>();
            if (entry.Value.TryGetProperty("Bones", out var bones) &&
                bones.ValueKind == JsonValueKind.Array)
            {
                foreach (var bone in bones.EnumerateArray())
                {
                    if (bone.GetString() is { Length: > 0 } prefix)
                        prefixes.Add(prefix);
                }
            }
            rows.Add(new ImportBoneCategory(id, name, prefixes.ToArray()));
        }
        if (groupName != null)
            groups.Add(new ImportBoneCategoryGroup(groupName, rows.ToArray()));
        return groups.ToArray();
    }

    /// <summary>The prefixes the named categories carry, as one exclusion set
    /// — how a fixed Brio option preset (BodyOptions' DisableCategory run)
    /// becomes a <see cref="PoseImportOptions.ExcludedBonePrefixes"/>. Ids the
    /// catalog does not know, and the slot rows that carry no prefixes,
    /// contribute nothing.</summary>
    public static HashSet<string> PrefixesFor(params string[] categoryIds)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in All)
        {
            if (Array.IndexOf(categoryIds, category.Id) < 0)
                continue;
            foreach (var prefix in category.Prefixes)
                prefixes.Add(prefix);
        }
        return prefixes;
    }

    /// <summary>Whether any of the named categories claims this bone —
    /// Brio's <c>IsBoneValidUncached</c> membership test (BoneFilter.cs:
    /// 127-142) restricted to one option preset's enabled set.</summary>
    public static bool IsInCategories(string boneName, params string[] categoryIds)
    {
        var categories = FlatCategories.Value;
        for (int i = 0; i < categories.Length; i++)
        {
            var category = categories[i];
            if (Array.IndexOf(categoryIds, category.Id) < 0)
                continue;
            foreach (var prefix in category.Prefixes)
            {
                if (boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Whether any category's prefix claims this bone — a bone no
    /// category claims falls to the "Other" row (Brio's _otherAllowed).</summary>
    public static bool IsCategorized(string boneName)
    {
        var categories = FlatCategories.Value;
        for (int i = 0; i < categories.Length; i++)
        {
            foreach (var prefix in categories[i].Prefixes)
            {
                if (boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The bone-filter menu's verdict folded into an options build — Brio's
    /// live <c>DefaultImporterOptions.BoneFilter</c> as this option model's
    /// exclusions. Disabled prefix categories become
    /// <see cref="PoseImportOptions.ExcludedBonePrefixes"/>, the three slot
    /// rows gate their slots (BoneFilter.cs:118-125, which answers by SLOT
    /// before it ever looks at a name), and a disabled "other" row bans every
    /// bone no category claims.
    ///
    /// <para>Lives here rather than in the popup so the fold the UI performs
    /// and the fold the tests pin are the same code.</para>
    /// </summary>
    public static PoseImportOptions ApplyDisabledCategories(
        PoseImportOptions options, IReadOnlySet<string> disabledCategoryIds)
    {
        if (disabledCategoryIds.Count == 0)
            return options;
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in All)
        {
            if (!disabledCategoryIds.Contains(category.Id))
                continue;
            foreach (var prefix in category.Prefixes)
                prefixes.Add(prefix);
        }
        if (prefixes.Count > 0)
            options.ExcludedBonePrefixes = prefixes;
        options.ExcludeUncategorizedBones = disabledCategoryIds.Contains("other");
        if (disabledCategoryIds.Contains("weapon"))
        {
            options.ApplyMainHand = false;
            options.ApplyOffHand = false;
        }
        if (disabledCategoryIds.Contains("prop"))
            options.ApplyProp = false;
        if (disabledCategoryIds.Contains("ornament"))
            options.ApplyOrnament = false;
        return options;
    }

    /// <summary>
    /// Ktisis' ear set, its exact 22 bones (PoseUtil.cs:6-21): the six
    /// standard ear bones plus the sixteen Viera ear-variant bones. Only the
    /// variant matching the actor's ear id is present on any one skeleton, so
    /// naming all four variants costs nothing and covers every Viera.
    ///
    /// <para>Whole bone names, used as PREFIXES. No other bone begins with any
    /// of them, so the prefix test the exclusion set performs is an exact
    /// match here.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> EarBones = Array.AsReadOnly(
        new[]
        {
            "j_mimi_l", "j_mimi_r",
            "n_ear_a_l", "n_ear_a_r",
            "n_ear_b_l", "n_ear_b_r",
            "j_zera_a_l", "j_zera_b_l", "j_zera_a_r", "j_zera_b_r",
            "j_zerb_a_l", "j_zerb_b_l", "j_zerb_a_r", "j_zerb_b_r",
            "j_zerc_a_l", "j_zerc_b_l", "j_zerc_a_r", "j_zerc_b_r",
            "j_zerd_a_l", "j_zerd_b_l", "j_zerd_a_r", "j_zerd_b_r",
        });

    /// <summary>
    /// Ktisis' standalone "Exclude ear bones" (PoseImportDialog.cs:176,
    /// PosingManager.cs:230-231), folded into whatever exclusions the build
    /// already carries. Deliberately independent of the category menu: that
    /// menu is dead the moment Body or Expression is checked, which is exactly
    /// when a user most wants the ears held back, so routing this through the
    /// existing "ears" category row would put it out of reach on the two
    /// common typed paths.
    /// </summary>
    public static PoseImportOptions ExcludeEarBones(PoseImportOptions options)
    {
        var prefixes = options.ExcludedBonePrefixes is { } existing
            ? new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bone in EarBones)
            prefixes.Add(bone);
        options.ExcludedBonePrefixes = prefixes;
        return options;
    }
}
