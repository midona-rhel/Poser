using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class EarBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Standard ears
        data["j_mimi_l"] = new("Ear Left");
        data["j_mimi_r"] = new("Ear Right");
        data["n_ear_a_l"] = new("Earring A Left");
        data["n_ear_a_r"] = new("Earring A Right");
        data["n_ear_b_l"] = new("Earring B Left");
        data["n_ear_b_r"] = new("Earring B Right");

        // Viera Ears (multiple variants)
        data["j_zera_a_l"] = new("Viera Ear A Left");
        data["j_zera_a_r"] = new("Viera Ear A Right");
        data["j_zera_b_l"] = new("Viera Ear B Left");
        data["j_zera_b_r"] = new("Viera Ear B Right");
        data["j_zerb_a_l"] = new("Viera Ear A Left");
        data["j_zerb_a_r"] = new("Viera Ear A Right");
        data["j_zerb_b_l"] = new("Viera Ear B Left");
        data["j_zerb_b_r"] = new("Viera Ear B Right");
        data["j_zerc_a_l"] = new("Viera Ear A Left");
        data["j_zerc_a_r"] = new("Viera Ear A Right");
        data["j_zerc_b_l"] = new("Viera Ear B Left");
        data["j_zerc_b_r"] = new("Viera Ear B Right");
        data["j_zerd_a_l"] = new("Viera Ear A Left");
        data["j_zerd_a_r"] = new("Viera Ear A Right");
        data["j_zerd_b_l"] = new("Viera Ear B Left");
        data["j_zerd_b_r"] = new("Viera Ear B Right");
    }
}
