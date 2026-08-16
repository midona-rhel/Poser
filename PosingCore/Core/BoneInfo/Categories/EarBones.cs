using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class EarBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Standard ears
        data["j_mimi_l"] = new("Ear Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_mimi_r"] = new("Ear Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["n_ear_a_l"] = new("Earring A Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["n_ear_a_r"] = new("Earring A Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["n_ear_b_l"] = new("Earring B Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["n_ear_b_r"] = new("Earring B Right", BoneCategory.Head, BoneSubcategory.Ears);

        // Viera Ears (multiple variants)
        data["j_zera_a_l"] = new("Viera Ear A Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zera_a_r"] = new("Viera Ear A Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zera_b_l"] = new("Viera Ear B Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zera_b_r"] = new("Viera Ear B Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerb_a_l"] = new("Viera Ear A Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerb_a_r"] = new("Viera Ear A Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerb_b_l"] = new("Viera Ear B Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerb_b_r"] = new("Viera Ear B Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerc_a_l"] = new("Viera Ear A Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerc_a_r"] = new("Viera Ear A Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerc_b_l"] = new("Viera Ear B Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerc_b_r"] = new("Viera Ear B Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerd_a_l"] = new("Viera Ear A Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerd_a_r"] = new("Viera Ear A Right", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerd_b_l"] = new("Viera Ear B Left", BoneCategory.Head, BoneSubcategory.Ears);
        data["j_zerd_b_r"] = new("Viera Ear B Right", BoneCategory.Head, BoneSubcategory.Ears);
    }
}
