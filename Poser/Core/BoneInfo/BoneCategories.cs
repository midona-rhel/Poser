using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;

namespace Poser.Core.BoneInfo;

/// <summary>
/// Bone category definition with color and bone list.
/// Copied exactly from Ktisis Category.cs
/// </summary>
public class BoneCategoryDef
{
    public string Name { get; }
    public Vector4 Color { get; }
    public IReadOnlyList<string> Bones { get; }
    public bool IsNsfw { get; }

    private BoneCategoryDef(string name, Vector4 color, List<string> bones, bool isNsfw = false)
    {
        Name = name;
        Color = color;
        Bones = new ReadOnlyCollection<string>(bones);
        IsNsfw = isNsfw;
    }

    private static readonly Dictionary<string, BoneCategoryDef> _categories = new();
    private static readonly Dictionary<string, List<BoneCategoryDef>> _boneToCategories = new();

    public static IReadOnlyDictionary<string, BoneCategoryDef> All => _categories;
    public static BoneCategoryDef Other => _categories["other"];

    public static List<BoneCategoryDef> GetForBone(string? boneName)
    {
        if (string.IsNullOrEmpty(boneName))
            return new List<BoneCategoryDef> { Other };

        if (_boneToCategories.TryGetValue(boneName, out var categories))
            return categories;

        // This is needed for bone names with an ID, such as hair bones, e.g. j_ex_h0116_ke_b_a
        var stripped = RemoveSpecialCharacters(boneName);
        if (_boneToCategories.TryGetValue(stripped, out categories))
            return categories;

        return new List<BoneCategoryDef> { Other };
    }

    public static BoneCategoryDef? GetByName(string name)
        => _categories.GetValueOrDefault(name);

    public static string RemoveSpecialCharacters(string str)
    {
        var buffer = new char[str.Length];
        int index = 0;
        foreach (char c in str)
        {
            if (c < '0' || c > '9')
            {
                buffer[index] = c;
                index++;
            }
        }
        return new string(buffer, 0, index);
    }

    private static BoneCategoryDef Create(string name, Vector4 color, List<string> bones, bool isNsfw = false)
    {
        var cat = new BoneCategoryDef(name, color, bones, isNsfw);
        _categories[name] = cat;

        foreach (var bone in bones)
        {
            if (!_boneToCategories.TryGetValue(bone, out var list))
            {
                list = new List<BoneCategoryDef>();
                _boneToCategories[bone] = list;
            }
            list.Add(cat);
        }

        return cat;
    }

    static BoneCategoryDef()
    {
        var defaultColor = new Vector4(1.0f, 1.0f, 1.0f, 0.5647059f);

        // Default fallback category (do not assign bones here)
        Create("other", new Vector4(1.0f, 1.0f, 1.0f, 0.5647059f), new List<string>());

        Create("body", new Vector4(1.0f, 0.0f, 0.0f, 0.5647059f), new List<string> {
            "n_root",
            "n_hara",    // Abdomen
            // "n_throw",// Throw, this is moving when using /throw with a snowball, so it goes into "other" bones for now
            "j_kosi",    // Waist
            "j_sebo_a",  // SpineA
            "j_asi_a_l", // LegLeft
            "j_asi_a_r", // LegRight
            "j_sebo_b",  // SpineB
            "j_asi_b_l", // KneeLeft
            "j_asi_b_r", // KneeRight
            "j_mune_l",  // BreastLeft
            "j_mune_r",  // BreastRight
            "j_sebo_c",  // SpineC
            "j_asi_c_l", // CalfLeft
            "j_asi_c_r", // CalfRight
            "j_kubi",    // Neck
            "j_sako_l",  // ClavicleLeft
            "j_sako_r",  // ClavicleRight
            "j_asi_d_l", // FootLeft
            "j_asi_d_r", // FootRight
            // "j_kao",  // Head
            "j_ude_a_l", // ArmLeft
            "j_ude_a_r", // ArmRight
            "j_asi_e_l", // ToesLeft
            "j_asi_e_r", // ToesRight
            "j_ude_b_l", // ForearmLeft
            "j_ude_b_r", // ForearmRight
            "n_hkata_l", // ShoulderLeft
            "n_hkata_r", // ShoulderRight
            "n_hhiji_l", // ElbowLeft
            "n_hhiji_r", // ElbowRight
            "j_kao",     // RootHead
            "j_ago"      // Jaw
        });

        Create("eyes", new Vector4(0.0f, 1.0f, 0.0f, 0.5647059f), new List<string> {
            "j_f_eye_l", // EyeLeft
            "j_f_eye_r", // EyeRight
            "j_f_mab_l",
            "j_f_eyepuru_l",
            "j_f_mabdn_01_l",
            "j_f_mabup_01_l",
            "j_f_mabdn_02out_l",
            "j_f_mabdn_03in_l",
            "j_f_mabup_02out_l",
            "j_f_mabup_03in_l",
            "j_f_mab_r",
            "j_f_eyepuru_r",
            "j_f_mabdn_01_r",
            "j_f_mabup_01_r",
            "j_f_mabdn_02out_r",
            "j_f_mabdn_03in_r",
            "j_f_mabup_02out_r",
            "j_f_mabup_03in_r",
            "j_f_mmayu_l",
            "j_f_miken_01_l",
            "j_f_miken_02_l",
            "j_f_mmayu_r",
            "j_f_miken_01_r",
            "j_f_miken_02_r",
            "j_f_mayu_l", // EyebrowLeft
            "j_f_mayu_r"  // EyebrowRight
        });

        Create("mouth", new Vector4(0.0f, 1.0f, 0.0f, 0.5647059f), new List<string> {
            "j_f_ago",
            "j_f_dago",
            "j_f_hagukiup",
            "j_f_hagukidn",
            "j_f_bero_01",
            "j_f_bero_02",
            "j_f_bero_03",
            "j_f_lip_l", // LipsLeft
            "j_f_lip_r", // LipsRight
            "j_f_dmlip_01_l",
            "j_f_dmlip_02_l",
            "j_f_umlip_01_l",
            "j_f_umlip_02_l",
            "j_f_dmlip_01_r",
            "j_f_dmlip_02_r",
            "j_f_umlip_01_r",
            "j_f_umlip_02_r",
            "j_f_dlip_01_l",
            "j_f_dlip_02_l",
            "j_f_ulip_01_l",
            "j_f_ulip_02_l",
            "j_f_dlip_01_r",
            "j_f_dlip_02_r",
            "j_f_ulip_01_r",
            "j_f_ulip_02_r",
            "j_f_uslip_l",
            "j_f_dslip_l",
            "j_f_uslip_r",
            "j_f_dslip_r"
        });

        Create("face", new Vector4(0.0f, 1.0f, 0.0f, 0.5647059f), new List<string> {
            "j_f_face",
            "j_f_hana", // Nose
            "j_f_hana_l",
            "j_f_hana_r",
            "j_f_uhana",
            "j_f_hoho_l", // CheekLeft
            "j_f_hoho_r", // CheekRight
            "j_f_dhoho_l",
            "j_f_dhoho_r",
            "j_f_shoho_l",
            "j_f_shoho_r",
            "j_f_dmemoto_l",
            "j_f_dmemoto_r",
            "j_f_dmiken_l",
            "j_f_dmiken_r"
        });

        Create("hair", new Vector4(0.0f, 0.0f, 1.0f, 0.5647059f), new List<string> {
            "j_kami_a",   // HairA
            "j_kami_f_l", // HairFrontLeft
            "j_kami_f_r", // HairFrontRight
            "j_kami_b",   // HairB
            "j_ex_met_va",
            // some are real, some are guessed by pattern
            // the bones below must be stripped from digits
            "j_ex_h_ke_a_l",
            "j_ex_h_ke_a_r",
            "j_ex_h_ke_a",
            "j_ex_h_ke_b_a",
            "j_ex_h_ke_b_b",
            "j_ex_h_ke_b_l",
            "j_ex_h_ke_b_r",
            "j_ex_h_ke_b",
            "j_ex_h_ke_c_l",
            "j_ex_h_ke_c_r",
            "j_ex_h_ke_c",
            "j_ex_h_ke_d",
            "j_ex_h_ke_da",
            "j_ex_h_ke_db",
            "j_ex_h_ke_e",
            "j_ex_h_ke_f_a",
            "j_ex_h_ke_f_b",
            "j_ex_h_ke_f_l",
            "j_ex_h_ke_f_r",
            "j_ex_h_ke_f",
            "j_ex_h_ke_l",
            "j_ex_h_ke_r",
            "j_ex_h_ke_s_l",
            "j_ex_h_ke_s_r",
            "j_ex_h_ke_s",
            "j_ex_h_ke_u_l",
            "j_ex_h_ke_u_r",
            "j_ex_h_ke_u"
        });

        Create("clothes", new Vector4(1.0f, 1.0f, 0.0f, 0.5647059f), new List<string> {
            "j_sk_b_b_l",    // ClothBackBLeft
            "j_sk_b_b_r",    // ClothBackBRight
            "j_sk_f_b_l",    // ClothFrontBLeft
            "j_sk_f_b_r",    // ClothFrontBRight
            "j_sk_s_b_l",    // ClothSideBLeft
            "j_sk_s_b_r",    // ClothSideBRight
            "j_sk_b_a_l",    // ClothBackALeft
            "j_sk_b_a_r",    // ClothBackARight
            "j_sk_f_a_l",    // ClothFrontALeft
            "j_sk_f_a_r",    // ClothFrontARight
            "j_sk_s_a_l",    // ClothSideALeft
            "j_sk_s_a_r",    // ClothSideARight
            "j_sk_b_c_l",    // ClothBackCLeft
            "j_sk_b_c_r",    // ClothBackCRight
            "j_sk_f_c_l",    // ClothFrontCLeft
            "j_sk_f_c_r",    // ClothFrontCRight
            "j_sk_s_c_l",    // ClothSideCLeft
            "j_sk_s_c_r",    // ClothSideCRight
            "n_hizasoubi_l", // PoleynLeft
            "n_hizasoubi_r", // PoleynRight
            "n_kataarmor_l", // PauldronLeft
            "n_kataarmor_r", // PauldronRight
            "n_hijisoubi_l", // CouterLeft
            "n_hijisoubi_r", // CouterRight
            "n_ear_a_l",     // EarringALeft
            "n_ear_a_r",     // EarringARight
            "n_ear_b_l",     // EarringBLeft
            "n_ear_b_r",     // EarringBRight
            "j_ex_top_a_r",
            "j_ex_top_a_l",
            "j_ex_top_b_r",
            "j_ex_top_b_l",
            "j_ex_met_a",
            "j_ex_met_b",
            "j_ex_met_c",
            "j_ex_met_d"
        });

        Create("weapons", new Vector4(1.0f, 0.0f, 1.0f, 0.5647059f), new List<string> {
            "j_buki_sebo_l",  // ScabbardLeft
            "j_buki_sebo_r",  // ScabbardRight
            "j_buki2_kosi_l", // HolsterLeft
            "j_buki2_kosi_r", // HolsterRight
            "j_buki_kosi_l",  // SheatheLeft
            "j_buki_kosi_r",  // SheatheRight
            "n_buki_r",       // WeaponRight
            "n_buki_l",       // WeaponLeft
            "n_buki_tate_l",  // ShieldLeft
            "n_buki_tate_r"   // ShieldRight
        });

        Create("right hand", new Vector4(1.0f, 0.0f, 1.0f, 0.5647059f), new List<string> {
            "j_hito_a_r", // IndexARight
            "j_ko_a_r",   // PinkyARight
            "j_kusu_a_r", // RingARight
            "j_naka_a_r", // MiddleARight
            "j_oya_a_r",  // ThumbARight
            "j_hito_b_r", // IndexBRight
            "j_ko_b_r",   // PinkyBRight
            "j_kusu_b_r", // RingBRight
            "j_naka_b_r", // MiddleBRight
            "j_oya_b_r",  // ThumbBRight
            "j_te_r",     // HandRight
            "n_hte_r"     // WristRight
        });

        Create("left hand", new Vector4(0.0f, 1.0f, 1.0f, 0.5647059f), new List<string> {
            "j_hito_a_l", // IndexALeft
            "j_ko_a_l",   // PinkyALeft
            "j_kusu_a_l", // RingALeft
            "j_naka_a_l", // MiddleALeft
            "j_oya_a_l",  // ThumbALeft
            "j_hito_b_l", // IndexBLeft
            "j_ko_b_l",   // PinkyBLeft
            "j_kusu_b_l", // RingBLeft
            "j_naka_b_l", // MiddleBLeft
            "j_oya_b_l",  // ThumbBLeft
            "j_te_l",     // HandLeft
            "n_hte_l"     // WristLeft
        });

        Create("tail", defaultColor, new List<string> {
            // Tail A-E
            "n_sippo_a",
            "n_sippo_b",
            "n_sippo_c",
            "n_sippo_d",
            "n_sippo_e"
        });

        Create("ears", defaultColor, new List<string> {
            "j_mimi_l",   // EarLeft
            "j_mimi_r",   // EarRight
            "j_zera_a_l", // VieraEar01ALeft
            "j_zera_a_r", // VieraEar01ARight
            "j_zera_b_l", // VieraEar01BLeft
            "j_zera_b_r", // VieraEar01BRight
            "j_zerb_a_l", // VieraEar02ALeft
            "j_zerb_a_r", // VieraEar02ARight
            "j_zerb_b_l", // VieraEar02BLeft
            "j_zerb_b_r", // VieraEar02BRight
            "j_zerc_a_l", // VieraEar03ALeft
            "j_zerc_a_r", // VieraEar03ARight
            "j_zerc_b_l", // VieraEar03BLeft
            "j_zerc_b_r", // VieraEar03BRight
            "j_zerd_a_l", // VieraEar04ALeft
            "j_zerd_a_r", // VieraEar04ARight
            "j_zerd_b_l", // VieraEar04BLeft
            "j_zerd_b_r"  // VieraEar04BRight
        });

        // IVCS Categories
        Create("ivcs left hand", defaultColor, new List<string> {
            "iv_ko_c_l",   // Pinky rotation
            "iv_kusu_c_l", // Ring
            "iv_naka_c_l", // Middle
            "iv_hito_c_l"  // Index
        });

        Create("ivcs right hand", defaultColor, new List<string> {
            "iv_ko_c_r",   // Pinky
            "iv_kusu_c_r", // Ring
            "iv_naka_c_r", // Middle
            "iv_hito_c_r"  // Index
        });

        Create("ivcs left foot", defaultColor, new List<string> {
            "iv_asi_oya_a_l",  // Big toe rotation
            "iv_asi_oya_b_l",  // Big toe
            "iv_asi_hito_a_l", // Index rotation
            "iv_asi_hito_b_l", // Index
            "iv_asi_naka_a_l", // Middle rotation
            "iv_asi_naka_b_l", // Middle
            "iv_asi_kusu_a_l", // Fore toe rotation
            "iv_asi_kusu_b_l", // Fore toe
            "iv_asi_ko_a_l",   // Pinky toe rotation
            "iv_asi_ko_b_l"    // Pinky toe
        });

        Create("ivcs right foot", defaultColor, new List<string> {
            "iv_asi_oya_a_r",  // Big toe
            "iv_asi_oya_b_r",  // Big toe
            "iv_asi_hito_a_r", // Index
            "iv_asi_hito_b_r", // Index
            "iv_asi_naka_a_r", // Middle
            "iv_asi_naka_b_r", // Middle
            "iv_asi_kusu_a_r", // Fore toe
            "iv_asi_kusu_b_r", // Fore toe
            "iv_asi_ko_a_r",   // Pinky toe
            "iv_asi_ko_b_r"    // Pinky toe
        });

        Create("ivcs body", defaultColor, new List<string> {
            // Biceps (rotation, scale, position)
            "iv_nitoukin_l",
            "iv_nitoukin_r",
            // Control override bones (override physics for animations only)
            // Breasts (rotation, scale, position)
            "iv_c_mune_l",
            "iv_c_mune_r"
        });

        Create("ivcs penis", defaultColor, new List<string> {
            // Scrotum (rotation, scale, position)
            "iv_kougan_l",
            "iv_kougan_r",
            // Penis (rotation, scale)
            "iv_ochinko_a",
            "iv_ochinko_b",
            "iv_ochinko_c",
            "iv_ochinko_d",
            "iv_ochinko_e",
            "iv_ochinko_f"
        }, true);

        Create("ivcs vagina", defaultColor, new List<string> {
            "iv_omanko",
            "iv_kuritto",  // Clitoris rotation, position, scale
            "iv_inshin_l", // Labia rotation, position, scale
            "iv_inshin_r"  // Labia
        }, true);

        Create("ivcs buttocks", defaultColor, new List<string> {
            // Anus (rotation, scale, position)
            "iv_koumon",
            "iv_koumon_l",
            "iv_koumon_r",
            // Buttocks (rotation, scale, position)
            "iv_shiri_l",
            "iv_shiri_r"
        }, true);

        // Compound categories
        // The categories below have bones in common with other categories above.

        // feet
        var feetBones = new List<string> {
            "j_asi_d_l", // FootLeft
            "j_asi_d_r", // FootRight
            "j_asi_e_l", // ToesLeft
            "j_asi_e_r"  // ToesRight
        };
        if (_categories.TryGetValue("ivcs left foot", out var ivcsLeftFoot))
            feetBones.AddRange(ivcsLeftFoot.Bones);
        if (_categories.TryGetValue("ivcs right foot", out var ivcsRightFoot))
            feetBones.AddRange(ivcsRightFoot.Bones);
        Create("feet", defaultColor, feetBones);

        // hands
        var handsBones = new List<string>();
        if (_categories.TryGetValue("right hand", out var rightHand))
            handsBones.AddRange(rightHand.Bones);
        if (_categories.TryGetValue("left hand", out var leftHand))
            handsBones.AddRange(leftHand.Bones);
        if (_categories.TryGetValue("ivcs left hand", out var ivcsLeftHand))
            handsBones.AddRange(ivcsLeftHand.Bones);
        if (_categories.TryGetValue("ivcs right hand", out var ivcsRightHand))
            handsBones.AddRange(ivcsRightHand.Bones);
        Create("hands", defaultColor, handsBones);
    }
}
