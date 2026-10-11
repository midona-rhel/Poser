using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class PhysicsBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // IVCS Physics bones - Spine category
        data["iv_kyokin_phys_r"] = new("IVCS Physics Breast Right");
        data["iv_kyokin_phys_l"] = new("IVCS Physics Breast Left");
        data["iv_kintama_phys_l"] = new("IVCS Physics Scrotum Left");
        data["iv_kintama_phys_r"] = new("IVCS Physics Scrotum Right");
        data["iv_funyachin_phy_a"] = new("IVCS Physics Penis A");
        data["iv_funyachin_phy_b"] = new("IVCS Physics Penis B");
        data["iv_funyachin_phy_c"] = new("IVCS Physics Penis C");
        data["iv_funyachin_phy_d"] = new("IVCS Physics Penis D");
        data["iv_fukubu_phys"] = new("IVCS Physics Abdomen");
        data["iv_fukubu_phys_l"] = new("IVCS Physics Abdomen Left");
        data["iv_fukubu_phys_r"] = new("IVCS Physics Abdomen Right");

        // IVCS Physics - Legs
        data["iv_daitai_phys_l"] = new("IVCS Physics Thigh Left");
        data["iv_daitai_phys_r"] = new("IVCS Physics Thigh Right");

        // YA (Yamaneko) Physics bones - Spine category
        data["ya_fukubu_phys"] = new("YA Physics Abdomen");
        data["ya_shiri_phys_l"] = new("YA Physics Buttock Left");
        data["ya_shiri_phys_r"] = new("YA Physics Buttock Right");

        // YA Physics - Legs
        data["ya_daitai_phys_l"] = new("YA Physics Thigh Left");
        data["ya_daitai_phys_r"] = new("YA Physics Thigh Right");
    }
}
