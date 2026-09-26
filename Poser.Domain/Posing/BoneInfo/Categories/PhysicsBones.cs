using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class PhysicsBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // IVCS Physics bones - Spine category
        data["iv_kyokin_phys_r"] = new("IVCS Physics Breast Right", BoneCategory.Spine);
        data["iv_kyokin_phys_l"] = new("IVCS Physics Breast Left", BoneCategory.Spine);
        data["iv_kintama_phys_l"] = new("IVCS Physics Scrotum Left", BoneCategory.Spine);
        data["iv_kintama_phys_r"] = new("IVCS Physics Scrotum Right", BoneCategory.Spine);
        data["iv_funyachin_phy_a"] = new("IVCS Physics Penis A", BoneCategory.Spine);
        data["iv_funyachin_phy_b"] = new("IVCS Physics Penis B", BoneCategory.Spine);
        data["iv_funyachin_phy_c"] = new("IVCS Physics Penis C", BoneCategory.Spine);
        data["iv_funyachin_phy_d"] = new("IVCS Physics Penis D", BoneCategory.Spine);
        data["iv_fukubu_phys"] = new("IVCS Physics Abdomen", BoneCategory.Spine);
        data["iv_fukubu_phys_l"] = new("IVCS Physics Abdomen Left", BoneCategory.Spine);
        data["iv_fukubu_phys_r"] = new("IVCS Physics Abdomen Right", BoneCategory.Spine);

        // IVCS Physics - Legs
        data["iv_daitai_phys_l"] = new("IVCS Physics Thigh Left", BoneCategory.LeftLeg);
        data["iv_daitai_phys_r"] = new("IVCS Physics Thigh Right", BoneCategory.RightLeg);

        // YA (Yamaneko) Physics bones - Spine category
        data["ya_fukubu_phys"] = new("YA Physics Abdomen", BoneCategory.Spine);
        data["ya_shiri_phys_l"] = new("YA Physics Buttock Left", BoneCategory.Spine);
        data["ya_shiri_phys_r"] = new("YA Physics Buttock Right", BoneCategory.Spine);

        // YA Physics - Legs
        data["ya_daitai_phys_l"] = new("YA Physics Thigh Left", BoneCategory.LeftLeg);
        data["ya_daitai_phys_r"] = new("YA Physics Thigh Right", BoneCategory.RightLeg);
    }
}
