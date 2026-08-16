// Generated from Ktisis Data/Schema/Categories.xml (clone @ origin/main,
// read 2026-08-11) by a one-off transcription; regenerate rather than edit.
// The sidebar's bone tree states THIS hierarchy (user 2026-08-11: Ktisis'
// categories and arrangement, verbatim).
using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

/// <summary>One node of the Ktisis category tree.</summary>
public sealed record KtisisBoneCategory(
    string Id,
    string Label,
    string[] Bones,
    KtisisBoneCategory[] Children);

public static class KtisisBoneCategories
{
    public static readonly KtisisBoneCategory[] Roots =
    [
        new("Head", "Head",
            ["j_kao", "j_kubi", "j_head"],
            [
            new("Hair", "Hair",
                ["j_kami_a", "j_kami_b", "j_kami_f_l", "j_kami_f_r"],
                []),
            new("Ears", "Ears",
                [],
                [
                new("LeftEar", "Left Ear",
                    ["j_mimi_l", "j_zera_a_l", "j_zera_b_l", "j_zerb_a_l", "j_zerb_b_l", "j_zerc_a_l", "j_zerc_b_l", "j_zerd_a_l", "j_zerd_b_l", "j_ear_l"],
                    []),
                new("RightEar", "Right Ear",
                    ["j_mimi_r", "j_zera_a_r", "j_zera_b_r", "j_zerb_a_r", "j_zerb_b_r", "j_zerc_a_r", "j_zerc_b_r", "j_zerd_a_r", "j_zerd_b_r", "j_ear_r"],
                    []),
                ]),
            new("Face", "Face",
                ["j_face", "j_f_face"],
                [
                new("Brow", "Brow",
                    [],
                    [
                    new("LeftBrow", "Left Brow",
                        ["j_f_miken_l", "j_f_mayu_l", "j_f_mmayu_l", "j_f_miken_01_l", "j_f_miken_02_l"],
                        []),
                    new("RightBrow", "Right Brow",
                        ["j_f_miken_r", "j_f_mayu_r", "j_f_mmayu_r", "j_f_miken_01_r", "j_f_miken_02_r"],
                        []),
                    ]),
                new("Eyes", "Eyes",
                    ["j_f_eyeprm_01_l", "j_f_eyeprm_01_r", "j_f_eyeprmroll_l", "j_f_eyeprmroll_r", "j_f_eyeprm_02_l", "j_f_eyeprm_02_r", "j_f_irisprm_l", "j_f_irisprm_r", "j_f_noanim_eyesize_l", "j_f_noanim_eyesize_r"],
                    [
                    new("LeftEye", "Left Eye",
                        ["j_f_eye_l", "j_f_eyepuru_l", "j_f_mab_l"],
                        [
                        new("LeftEyelid", "Left Eyelid",
                            ["j_f_umab_l", "j_f_dmab_l", "j_f_mabup_01_l", "j_f_mabdn_01_l", "j_f_mabup_02out_l", "j_f_mabdn_02out_l", "j_f_mabup_03in_l", "j_f_mabdn_03in_l"],
                            []),
                        ]),
                    new("RightEye", "Right Eye",
                        ["j_f_eye_r", "j_f_eyepuru_r", "j_f_mab_r"],
                        [
                        new("RightEyelid", "Right Eyelid",
                            ["j_f_umab_r", "j_f_dmab_r", "j_f_mabup_01_r", "j_f_mabdn_01_r", "j_f_mabup_02out_r", "j_f_mabdn_02out_r", "j_f_mabup_03in_r", "j_f_mabdn_03in_r"],
                            []),
                        ]),
                    ]),
                new("Nose", "Nose",
                    ["j_f_hana", "j_f_memoto", "j_f_uhana", "j_f_hana_l", "j_f_hana_r"],
                    []),
                new("Cheeks", "Cheeks",
                    [],
                    [
                    new("LeftCheek", "Left Cheek",
                        ["j_f_hoho_l", "j_f_dhoho_l", "j_f_shoho_l", "j_f_dmemoto_l"],
                        []),
                    new("RightCheek", "Right Cheek",
                        ["j_f_hoho_r", "j_f_dhoho_r", "j_f_shoho_r", "j_f_dmemoto_r"],
                        []),
                    ]),
                new("Mouth", "Mouth",
                    ["j_ago", "j_f_lip_l", "j_f_lip_r"],
                    [
                    new("UpperMouth", "Upper Mouth",
                        ["j_f_hagukiup"],
                        [
                        new("UpperLip", "Upper Lip",
                            ["j_f_ulip_a", "j_f_ulip_b", "j_f_ulip_01_l", "j_f_ulip_02_l", "j_f_ulip_01_r", "j_f_ulip_02_r", "j_f_umlip_01_l", "j_f_umlip_02_l", "j_f_umlip_01_r", "j_f_umlip_02_r", "j_f_uslip_l", "j_f_uslip_r"],
                            []),
                        ]),
                    new("LowerMouth", "Lower Mouth",
                        ["j_f_ago", "j_f_dago", "j_f_hagukidn"],
                        [
                        new("LowerLip", "Lower Lip",
                            ["j_f_dlip_a", "j_f_dlip_b", "j_f_dlip_01_l", "j_f_dlip_02_l", "j_f_dlip_01_r", "j_f_dlip_02_r", "j_f_dmlip_01_l", "j_f_dmlip_02_l", "j_f_dmlip_01_r", "j_f_dmlip_02_r", "j_f_dslip_l", "j_f_dslip_r"],
                            []),
                        ]),
                    new("Tongue", "Tongue",
                        ["j_f_bero_01", "j_f_bero_02", "j_f_bero_03"],
                        []),
                    ]),
                ]),
            ]),
        new("Body", "Body",
            [],
            [
            new("Arms", "Arms",
                [],
                [
                new("CustomArms", "Custom Arms",
                    ["iv_nitoukin_r", "iv_nitoukin_l"],
                    []),
                new("LeftArm", "Left Arm",
                    ["j_sako_l", "j_ude_a_l", "j_ude_b_l", "n_hkata_l", "n_hhiji_l", "iv_nitoukin_l"],
                    [
                    new("LeftHand", "Left Hand",
                        ["j_hito_a_l", "j_ko_a_l", "j_kusu_a_l", "j_naka_a_l", "j_oya_a_l", "j_hito_b_l", "j_ko_b_l", "j_kusu_b_l", "j_naka_b_l", "j_oya_b_l", "j_te_l", "n_hte_l", "j_hand_l"],
                        [
                        new("LeftHandIvcs", "Left Hand IVCS",
                            ["iv_ko_c_l", "iv_kusu_c_l", "iv_naka_c_l", "iv_hito_c_l"],
                            []),
                        ]),
                    ]),
                new("RightArm", "Right Arm",
                    ["j_sako_r", "j_ude_a_r", "j_ude_b_r", "n_hkata_r", "n_hhiji_r", "iv_nitoukin_r"],
                    [
                    new("RightHand", "Right Hand",
                        ["j_hito_a_r", "j_ko_a_r", "j_kusu_a_r", "j_naka_a_r", "j_oya_a_r", "j_hito_b_r", "j_ko_b_r", "j_kusu_b_r", "j_naka_b_r", "j_oya_b_r", "j_te_r", "n_hte_r", "j_hand_r"],
                        [
                        new("RightHandIvcs", "Right Hand IVCS",
                            ["iv_ko_c_r", "iv_kusu_c_r", "iv_naka_c_r", "iv_hito_c_r"],
                            []),
                        ]),
                    ]),
                ]),
            new("Spine", "Spine",
                ["n_hara", "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c"],
                [
                new("SkelomaeWings", "Wings",
                    [],
                    [
                    new("SkelomaeWingsLeft", "Wings Left",
                        ["mkl_wingbase_l", "mkl_wingarm_a_l", "mkl_wingarm_b_l", "mkl_wingarm_c_l", "mkl_wingarm_d_l"],
                        []),
                    new("SkelomaeWingsRight", "Wings Right",
                        ["mkl_wingbase_r", "mkl_wingarm_a_r", "mkl_wingarm_b_r", "mkl_wingarm_c_r", "mkl_wingarm_d_r"],
                        []),
                    ]),
                new("Breasts", "Breasts",
                    ["j_mune_l", "j_mune_r"],
                    [
                    new("BreastsIvcs", "Breasts IVCS",
                        ["iv_c_mune_l", "iv_c_mune_r", "iv_kyokin_phys_l", "iv_kyokin_phys_r"],
                        []),
                    ]),
                new("SkelomaeCreatures", "Creatures",
                    [],
                    [
                    new("SkelomaeCreaturesLamia", "Creatures Lamia",
                        ["lamia_a", "lamia_b", "lamia_c", "lamia_d", "lamia_e", "lamia_f", "lamia_g", "lamia_h", "lamia_i", "lamia_j", "lamia_k", "lamia_l", "lamia_m", "lamia_n", "lamia_o", "lamia_p", "lamia_q", "lamia_r", "lamia_s", "lamia_t", "lamia_u"],
                        [
                        new("SkelomaeCreaturesGenetals", "Creatures Genetals",
                            ["lamia_clucoa_l", "lamia_clucoa_r"],
                            []),
                        ]),
                    new("SkelomaeCreaturesCentaur", "Creatures Centaur",
                        [],
                        [
                        new("SkelomaeCreaturesCentaurBody", "Creatures Centaur Body",
                            ["rf_centaur_body", "rf_centaur_hip", "rf_centaur_shoulder_f_l", "rf_centaur_shoulder_f_r"],
                            []),
                        new("SkelomaeCreaturesCentaurTail", "Creatures Centaur Tail",
                            ["rf_centaur_tail_a", "rf_centaur_tail_b", "rf_centaur_tail_c", "rf_centaur_tail_d"],
                            []),
                        new("SkelomaeCreaturesCentaurBodyFrontLegs", "Creatures Centaur Body Front Legs",
                            ["rf_centaur_farm_a_l", "rf_centaur_farm_b_l", "rf_centaur_farm_c_l", "rf_centaur_farm_d_l", "rf_centaur_farm_e_l", "rf_centaur_farm_a_r", "rf_centaur_farm_b_r", "rf_centaur_farm_c_r", "rf_centaur_farm_d_r", "rf_centaur_farm_e_r"],
                            []),
                        new("SkelomaeCreaturesCentaurBodyBackLegs", "Creatures Centaur Body Back Legs",
                            ["rf_centaur_bleg_a_l", "rf_centaur_bleg_b_l", "rf_centaur_bleg_c_l", "rf_centaur_bleg_d_l", "rf_centaur_bleg_e_l", "rf_centaur_bleg_a_r", "rf_centaur_bleg_b_r", "rf_centaur_bleg_c_r", "rf_centaur_bleg_d_r", "rf_centaur_bleg_e_r"],
                            []),
                        ]),
                    ]),
                new("CustomAbdomen", "Custom Abdomen",
                    ["iv_fukubu_phys", "ya_fukubu_phys", "iv_fukubu_phys_l", "iv_fukubu_phys_r"],
                    []),
                ]),
            new("Legs", "Legs",
                [],
                [
                new("CustomLegs", "Custom Legs",
                    ["iv_daitai_phys_l", "iv_daitai_phys_r", "ya_daitai_phys_l", "ya_daitai_phys_r"],
                    []),
                new("LeftLeg", "Left Leg",
                    ["j_asi_a_l", "j_asi_b_l", "j_asi_c_l"],
                    [
                    new("LeftFoot", "Left Foot",
                        ["j_asi_d_l", "j_asi_e_l", "j_foot_l"],
                        [
                        new("LeftFootIvcs", "Left Foot IVCS",
                            ["iv_asi_oya_a_l", "iv_asi_oya_b_l", "iv_asi_hito_a_l", "iv_asi_hito_b_l", "iv_asi_naka_a_l", "iv_asi_naka_b_l", "iv_asi_kusu_a_l", "iv_asi_kusu_b_l", "iv_asi_ko_a_l", "iv_asi_ko_b_l"],
                            []),
                        ]),
                    ]),
                new("RightLeg", "Right Leg",
                    ["j_asi_a_r", "j_asi_b_r", "j_asi_c_r"],
                    [
                    new("RightFoot", "Right Foot",
                        ["j_asi_d_r", "j_asi_e_r", "j_foot_r"],
                        [
                        new("RightFootIvcs", "Right Foot IVCS",
                            ["iv_asi_oya_a_r", "iv_asi_oya_b_r", "iv_asi_hito_a_r", "iv_asi_hito_b_r", "iv_asi_naka_a_r", "iv_asi_naka_b_r", "iv_asi_kusu_a_r", "iv_asi_kusu_b_r", "iv_asi_ko_a_r", "iv_asi_ko_b_r"],
                            []),
                        ]),
                    ]),
                ]),
            new("GenitalsIvcs", "Genitals IVCS",
                [],
                [
                new("PenisIvcs", "Penis IVCS",
                    ["iv_kougan_l", "iv_kougan_r", "iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f", "j_penis", "j_balls", "iv_funyachin_phy_a", "iv_funyachin_phy_b", "iv_funyachin_phy_c", "iv_funyachin_phy_d", "iv_kintama_phys_l", "iv_kintama_phys_r"],
                    []),
                new("VaginaIvcs", "Vagina IVCS",
                    ["iv_omanko", "iv_kuritto", "iv_inshin_l", "iv_inshin_r"],
                    []),
                ]),
            new("BottomIvcs", "Bottom IVCS",
                ["iv_koumon", "iv_koumon_l", "iv_koumon_r", "iv_shiri_l", "iv_shiri_r", "ya_shiri_phys_l", "ya_shiri_phys_r"],
                []),
            ]),
        new("Tail", "Tail",
            ["n_sippo_a", "n_sippo_b", "n_sippo_c", "n_sippo_d", "n_sippo_e", "j_tail"],
            []),
        new("Clothing", "Clothing",
            ["n_hizasoubi_l", "n_hizasoubi_r", "n_kataarmor_l", "n_kataarmor_r", "n_hijisoubi_l", "n_hijisoubi_r", "j_ex_top_a_r", "j_ex_top_a_l", "j_ex_top_b_r", "j_ex_top_b_l", "j_ex_met_a", "j_ex_met_b", "j_ex_met_c", "j_ex_met_d", "j_ex_met_va", "j_ex_met_vb"],
            [
            new("Cloth", "Cloth",
                ["j_sk_b_b_l", "j_sk_b_b_r", "j_sk_f_b_l", "j_sk_f_b_r", "j_sk_s_b_l", "j_sk_s_b_r", "j_sk_b_a_l", "j_sk_b_a_r", "j_sk_f_a_l", "j_sk_f_a_r", "j_sk_s_a_l", "j_sk_s_a_r", "j_sk_b_c_l", "j_sk_b_c_r", "j_sk_f_c_l", "j_sk_f_c_r", "j_sk_s_c_l", "j_sk_s_c_r"],
                []),
            new("Earring", "Earring",
                ["n_ear_a_l", "n_ear_a_r", "n_ear_b_l", "n_ear_b_r"],
                []),
            ]),
        new("Weapons", "Weapons",
            ["j_buki_sebo_l", "j_buki_sebo_r", "j_buki2_kosi_l", "j_buki2_kosi_r", "j_buki_kosi_l", "j_buki_kosi_r", "n_buki_r", "n_buki_l", "n_buki_tate_l", "n_buki_tate_r"],
            []),
        new("Other", "Other",
            [],
            []),
    ];

    /// <summary>Bone name -> owning category id, leaf-most wins; built once.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OwnerByBone { get; } =
        BuildOwners();

    private static Dictionary<string, string> BuildOwners()
    {
        var owners = new Dictionary<string, string>();
        void Walk(KtisisBoneCategory category)
        {
            foreach (var bone in category.Bones)
                owners[bone] = category.Id;
            foreach (var child in category.Children)
                Walk(child);
        }
        foreach (var root in Roots)
            Walk(root);
        return owners;
    }
}
