using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class IVCSBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Buttocks/Groin - Spine category
        data["iv_shiri_r"] = new("IVCS Buttock Right");
        data["iv_shiri_l"] = new("IVCS Buttock Left");
        data["iv_koumon_r"] = new("IVCS Anus Right");
        data["iv_koumon_l"] = new("IVCS Anus Left");
        data["iv_koumon"] = new("IVCS Anus");

        // Female genitalia - Spine category
        data["iv_omanko"] = new("IVCS Vagina");
        data["iv_inshin_r"] = new("IVCS Labia Right");
        data["iv_inshin_l"] = new("IVCS Labia Left");
        data["iv_kuritto"] = new("IVCS Clitoris");

        // Male genitalia - Spine category
        data["iv_ochinko_a"] = new("IVCS Penis A");
        data["iv_ochinko_b"] = new("IVCS Penis B");
        data["iv_ochinko_c"] = new("IVCS Penis C");
        data["iv_ochinko_d"] = new("IVCS Penis D");
        data["iv_ochinko_e"] = new("IVCS Penis E");
        data["iv_ochinko_f"] = new("IVCS Penis F");
        data["iv_kougan_r"] = new("IVCS Scrotum Right");
        data["iv_kougan_l"] = new("IVCS Scrotum Left");

        // Chest - Spine category
        data["iv_c_mune_r"] = new("IVCS Breast Right");
        data["iv_c_mune_l"] = new("IVCS Breast Left");

        // Arms - Right/Left Arm categories
        data["iv_nitoukin_r"] = new("IVCS Bicep Right");
        data["iv_nitoukin_l"] = new("IVCS Bicep Left");

        // Fingers - Right/Left Arm categories (hands are part of arms)
        data["iv_ko_c_l"] = new("IVCS Pinky C Left");
        data["iv_kusu_c_l"] = new("IVCS Ring C Left");
        data["iv_naka_c_l"] = new("IVCS Middle C Left");
        data["iv_hito_c_l"] = new("IVCS Index C Left");
        data["iv_ko_c_r"] = new("IVCS Pinky C Right");
        data["iv_kusu_c_r"] = new("IVCS Ring C Right");
        data["iv_naka_c_r"] = new("IVCS Middle C Right");
        data["iv_hito_c_r"] = new("IVCS Index C Right");

        // Toes - Left Leg category
        data["iv_asi_oya_a_l"] = new("IVCS Big Toe A Left");
        data["iv_asi_oya_b_l"] = new("IVCS Big Toe B Left");
        data["iv_asi_hito_a_l"] = new("IVCS Index Toe A Left");
        data["iv_asi_hito_b_l"] = new("IVCS Index Toe B Left");
        data["iv_asi_naka_a_l"] = new("IVCS Middle Toe A Left");
        data["iv_asi_naka_b_l"] = new("IVCS Middle Toe B Left");
        data["iv_asi_kusu_a_l"] = new("IVCS Fore Toe A Left");
        data["iv_asi_kusu_b_l"] = new("IVCS Fore Toe B Left");
        data["iv_asi_ko_a_l"] = new("IVCS Pinky Toe A Left");
        data["iv_asi_ko_b_l"] = new("IVCS Pinky Toe B Left");

        // Toes - Right Leg category
        data["iv_asi_oya_a_r"] = new("IVCS Big Toe A Right");
        data["iv_asi_oya_b_r"] = new("IVCS Big Toe B Right");
        data["iv_asi_hito_a_r"] = new("IVCS Index Toe A Right");
        data["iv_asi_hito_b_r"] = new("IVCS Index Toe B Right");
        data["iv_asi_naka_a_r"] = new("IVCS Middle Toe A Right");
        data["iv_asi_naka_b_r"] = new("IVCS Middle Toe B Right");
        data["iv_asi_kusu_a_r"] = new("IVCS Fore Toe A Right");
        data["iv_asi_kusu_b_r"] = new("IVCS Fore Toe B Right");
        data["iv_asi_ko_a_r"] = new("IVCS Pinky Toe A Right");
        data["iv_asi_ko_b_r"] = new("IVCS Pinky Toe B Right");
    }
}
