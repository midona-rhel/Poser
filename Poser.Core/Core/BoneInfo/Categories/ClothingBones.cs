using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class ClothingBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Cloth/Skirt - Back
        data["j_sk_b_a_l"] = new("Cloth Back A Left", BoneCategory.Equipment);
        data["j_sk_b_a_r"] = new("Cloth Back A Right", BoneCategory.Equipment);
        data["j_sk_b_b_l"] = new("Cloth Back B Left", BoneCategory.Equipment);
        data["j_sk_b_b_r"] = new("Cloth Back B Right", BoneCategory.Equipment);
        data["j_sk_b_c_l"] = new("Cloth Back C Left", BoneCategory.Equipment);
        data["j_sk_b_c_r"] = new("Cloth Back C Right", BoneCategory.Equipment);

        // Cloth/Skirt - Front
        data["j_sk_f_a_l"] = new("Cloth Front A Left", BoneCategory.Equipment);
        data["j_sk_f_a_r"] = new("Cloth Front A Right", BoneCategory.Equipment);
        data["j_sk_f_b_l"] = new("Cloth Front B Left", BoneCategory.Equipment);
        data["j_sk_f_b_r"] = new("Cloth Front B Right", BoneCategory.Equipment);
        data["j_sk_f_c_l"] = new("Cloth Front C Left", BoneCategory.Equipment);
        data["j_sk_f_c_r"] = new("Cloth Front C Right", BoneCategory.Equipment);

        // Cloth/Skirt - Side
        data["j_sk_s_a_l"] = new("Cloth Side A Left", BoneCategory.Equipment);
        data["j_sk_s_a_r"] = new("Cloth Side A Right", BoneCategory.Equipment);
        data["j_sk_s_b_l"] = new("Cloth Side B Left", BoneCategory.Equipment);
        data["j_sk_s_b_r"] = new("Cloth Side B Right", BoneCategory.Equipment);
        data["j_sk_s_c_l"] = new("Cloth Side C Left", BoneCategory.Equipment);
        data["j_sk_s_c_r"] = new("Cloth Side C Right", BoneCategory.Equipment);
    }
}
