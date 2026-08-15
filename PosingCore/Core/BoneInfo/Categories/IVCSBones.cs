using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class IVCSBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Buttocks/Groin - Spine category
        data["iv_shiri_r"] = new("IVCS Buttock Right", BoneCategory.Spine);
        data["iv_shiri_l"] = new("IVCS Buttock Left", BoneCategory.Spine);
        data["iv_koumon_r"] = new("IVCS Anus Right", BoneCategory.Spine);
        data["iv_koumon_l"] = new("IVCS Anus Left", BoneCategory.Spine);
        data["iv_koumon"] = new("IVCS Anus", BoneCategory.Spine);

        // Female genitalia - Spine category
        data["iv_omanko"] = new("IVCS Vagina", BoneCategory.Spine);
        data["iv_inshin_r"] = new("IVCS Labia Right", BoneCategory.Spine);
        data["iv_inshin_l"] = new("IVCS Labia Left", BoneCategory.Spine);
        data["iv_kuritto"] = new("IVCS Clitoris", BoneCategory.Spine);

        // Male genitalia - Spine category
        data["iv_ochinko_a"] = new("IVCS Penis A", BoneCategory.Spine);
        data["iv_ochinko_b"] = new("IVCS Penis B", BoneCategory.Spine);
        data["iv_ochinko_c"] = new("IVCS Penis C", BoneCategory.Spine);
        data["iv_ochinko_d"] = new("IVCS Penis D", BoneCategory.Spine);
        data["iv_ochinko_e"] = new("IVCS Penis E", BoneCategory.Spine);
        data["iv_ochinko_f"] = new("IVCS Penis F", BoneCategory.Spine);
        data["iv_kougan_r"] = new("IVCS Scrotum Right", BoneCategory.Spine);
        data["iv_kougan_l"] = new("IVCS Scrotum Left", BoneCategory.Spine);

        // Chest - Spine category
        data["iv_c_mune_r"] = new("IVCS Breast Right", BoneCategory.Spine);
        data["iv_c_mune_l"] = new("IVCS Breast Left", BoneCategory.Spine);

        // Arms - Right/Left Arm categories
        data["iv_nitoukin_r"] = new("IVCS Bicep Right", BoneCategory.RightArm);
        data["iv_nitoukin_l"] = new("IVCS Bicep Left", BoneCategory.LeftArm);

        // Fingers - Right/Left Arm categories (hands are part of arms)
        data["iv_ko_c_l"] = new("IVCS Pinky C Left", BoneCategory.LeftArm);
        data["iv_kusu_c_l"] = new("IVCS Ring C Left", BoneCategory.LeftArm);
        data["iv_naka_c_l"] = new("IVCS Middle C Left", BoneCategory.LeftArm);
        data["iv_hito_c_l"] = new("IVCS Index C Left", BoneCategory.LeftArm);
        data["iv_ko_c_r"] = new("IVCS Pinky C Right", BoneCategory.RightArm);
        data["iv_kusu_c_r"] = new("IVCS Ring C Right", BoneCategory.RightArm);
        data["iv_naka_c_r"] = new("IVCS Middle C Right", BoneCategory.RightArm);
        data["iv_hito_c_r"] = new("IVCS Index C Right", BoneCategory.RightArm);

        // Toes - Left Leg category
        data["iv_asi_oya_a_l"] = new("IVCS Big Toe A Left", BoneCategory.LeftLeg);
        data["iv_asi_oya_b_l"] = new("IVCS Big Toe B Left", BoneCategory.LeftLeg);
        data["iv_asi_hito_a_l"] = new("IVCS Index Toe A Left", BoneCategory.LeftLeg);
        data["iv_asi_hito_b_l"] = new("IVCS Index Toe B Left", BoneCategory.LeftLeg);
        data["iv_asi_naka_a_l"] = new("IVCS Middle Toe A Left", BoneCategory.LeftLeg);
        data["iv_asi_naka_b_l"] = new("IVCS Middle Toe B Left", BoneCategory.LeftLeg);
        data["iv_asi_kusu_a_l"] = new("IVCS Fore Toe A Left", BoneCategory.LeftLeg);
        data["iv_asi_kusu_b_l"] = new("IVCS Fore Toe B Left", BoneCategory.LeftLeg);
        data["iv_asi_ko_a_l"] = new("IVCS Pinky Toe A Left", BoneCategory.LeftLeg);
        data["iv_asi_ko_b_l"] = new("IVCS Pinky Toe B Left", BoneCategory.LeftLeg);

        // Toes - Right Leg category
        data["iv_asi_oya_a_r"] = new("IVCS Big Toe A Right", BoneCategory.RightLeg);
        data["iv_asi_oya_b_r"] = new("IVCS Big Toe B Right", BoneCategory.RightLeg);
        data["iv_asi_hito_a_r"] = new("IVCS Index Toe A Right", BoneCategory.RightLeg);
        data["iv_asi_hito_b_r"] = new("IVCS Index Toe B Right", BoneCategory.RightLeg);
        data["iv_asi_naka_a_r"] = new("IVCS Middle Toe A Right", BoneCategory.RightLeg);
        data["iv_asi_naka_b_r"] = new("IVCS Middle Toe B Right", BoneCategory.RightLeg);
        data["iv_asi_kusu_a_r"] = new("IVCS Fore Toe A Right", BoneCategory.RightLeg);
        data["iv_asi_kusu_b_r"] = new("IVCS Fore Toe B Right", BoneCategory.RightLeg);
        data["iv_asi_ko_a_r"] = new("IVCS Pinky Toe A Right", BoneCategory.RightLeg);
        data["iv_asi_ko_b_r"] = new("IVCS Pinky Toe B Right", BoneCategory.RightLeg);
    }
}
