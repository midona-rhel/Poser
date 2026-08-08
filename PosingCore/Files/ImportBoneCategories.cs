using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Files;

/// <summary>One row of the bone-filter menu: a named set of bone-name
/// prefixes (Brio BoneCategories.json, Filter entries).</summary>
public sealed record ImportBoneCategory(string Id, string Name, string[] Prefixes);

/// <summary>One group header of the bone-filter menu (Brio's Category
/// entries): Head Bones, Body Bones, IVCS Bones, Other Bones.</summary>
public sealed record ImportBoneCategoryGroup(string Name, ImportBoneCategory[] Categories);

/// <summary>
/// Brio's bone-filter catalog (Resources/Embedded/Data/BoneCategories.json),
/// verbatim prefixes with the display names its filter popup shows. The
/// "weapon", "prop" and "ornament" rows carry no prefixes in Brio either —
/// they gate whole slots and map onto the ApplyMainHand/OffHand/Prop/
/// Ornament options; every other row compiles to a name-prefix exclusion.
/// "Other" is the catch-all: a bone matching no prefix is governed by it
/// (Brio BoneFilter's _otherAllowed).
/// </summary>
public static class ImportBoneCategories
{
    public static readonly ImportBoneCategoryGroup[] Groups =
    {
        new("Head Bones", new ImportBoneCategory[]
        {
            new("hair", "Hair", new[] { "j_ex_h", "j_kami_", "j_ex_met_va" }),
            new("head", "Head", new[] { "j_kao", "j_f_face" }),
            new("face", "Face", new[]
            {
                "j_f_miken_", "j_f_mmayu_", "j_f_mayu_", "j_f_dhoho_",
                "j_f_shoho_", "j_f_dmemoto_", "j_f_hoho_", "j_f_uhana",
                "j_f_dmiken_", "j_f_hana_",
            }),
            new("eyes", "Eyes", new[]
            {
                "j_f_eye_", "j_f_eyepuru_", "j_f_mabdn_", "j_f_mabup_", "j_f_mab_",
            }),
            new("lips", "Lips", new[]
            {
                "j_f_dmlip_", "j_f_umlip_", "j_f_dlip_", "j_f_ulip_",
                "j_f_uslip_", "j_f_dslip_",
            }),
            new("jaw", "Jaw", new[]
            {
                "j_f_ago", "j_f_dago", "j_f_bero_", "j_f_hagukidn", "j_f_hagukiup",
            }),
            new("ears", "Ears", new[]
            {
                "n_ear_", "j_mimi", "j_zer",
            }),
        }),
        new("Body Bones", new ImportBoneCategory[]
        {
            new("body", "Body", new[]
            {
                "n_root", "n_hara", "j_kosi", "j_kubi", "j_sebo_", "j_mune_", "j_sako_",
            }),
            // Brio's ids are swapped relative to the sides they contain;
            // the DISPLAY names follow its popup (handsLeft holds _r bones).
            new("handsLeft", "Right Arm", new[]
            {
                "j_ude_a_r", "j_ude_b_r", "j_ko_", "n_hhiji_r", "j_hito_",
                "j_kusu_", "j_oya_", "j_naka_", "j_te_r", "j_hand_r",
                "n_hte_r", "n_hkata_r",
            }),
            new("handsRight", "Left Arm", new[]
            {
                "j_ude_a_l", "j_ude_b_l", "n_hhiji_l", "j_te_l", "j_hand_l",
                "n_hte_l", "n_hkata_l",
            }),
            new("legs", "Legs", new[] { "j_asi_" }),
            new("tail", "Tail", new[] { "n_sippo_" }),
        }),
        new("IVCS Bones", new ImportBoneCategory[]
        {
            new("ivcsAbdomen", "IVCS Breast & Abdomen", new[]
            {
                "iv_c_mune_", "iv_kyokin_phys_", "iv_fukubu_phys",
            }),
            new("ivcsHandsRight", "IVCS Right Arm", new[]
            {
                "iv_ko_c_r", "iv_kusu_c_r", "iv_naka_c_r", "iv_hito_c_r", "iv_nitoukin_r",
            }),
            new("ivcsHandsLeft", "IVCS Left Arm", new[]
            {
                "iv_ko_c_l", "iv_kusu_c_l", "iv_naka_c_l", "iv_hito_c_l", "iv_nitoukin_l",
            }),
            new("ivcsLegs", "IVCS Legs & Feet", new[] { "iv_daitai_phys_", "iv_asi_" }),
            new("ivcsButt", "IVCS Butt", new[]
            {
                "iv_koumon", "iv_shiri_", "ya_shiri_phys_",
            }),
            new("ivcsPenis", "IVCS Penis", new[]
            {
                "iv_kougan_", "iv_ochinko_", "j_penis", "j_balls",
                "iv_funyachin_phy_", "iv_kintama_phys_",
            }),
            new("ivcsVagina", "IVCS Vagina", new[]
            {
                "iv_omanko", "iv_kuritto", "iv_inshin_",
            }),
        }),
        new("Other Bones", new ImportBoneCategory[]
        {
            new("clothing", "Clothes", new[]
            {
                "n_hijisoubi_", "n_hizasoubi_", "n_kataarmor_", "j_sk_",
                "j_ex_top_", "j_ex_met_a", "j_ex_met_b", "j_ex_met_c",
                "j_ex_met_d", "j_zacc",
            }),
            new("weapon", "Weapons", Array.Empty<string>()),
            new("prop", "Emote Props", Array.Empty<string>()),
            new("ornament", "Fashion Accessories", Array.Empty<string>()),
            new("ex", "Dawntrail Other", new[]
            {
                "j_f_noanim_ago", "j_f_eyeprm", "j_f_irisprm",
                "j_f_noanim_eyesize_", "j_f_eyeprmroll_r",
            }),
            new("legacy", "Legacy", new[]
            {
                "j_ago", "j_f_dmab_", "j_f_umab_", "j_f_memoto", "j_f_miken_r",
                "j_f_miken_l", "j_f_ulip_b", "j_f_lip_", "j_f_dlip_b",
                "j_f_dlip_a", "j_f_ulip_a",
            }),
            new("other", "Other", Array.Empty<string>()),
        }),
    };

    /// <summary>Every prefix-carrying category, flattened.</summary>
    public static IEnumerable<ImportBoneCategory> All =>
        Groups.SelectMany(group => group.Categories);

    /// <summary>Whether any category's prefix claims this bone — a bone no
    /// category claims falls to the "Other" row (Brio's _otherAllowed).</summary>
    public static bool IsCategorized(string boneName)
    {
        foreach (var category in All)
        {
            foreach (var prefix in category.Prefixes)
            {
                if (boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
