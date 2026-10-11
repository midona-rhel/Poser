using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class LeftHandBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_te_l"] = new("Hand Left");
        data["n_hte_l"] = new("Wrist Left");

        // Fingers
        data["j_hito_a_l"] = new("Index A Left");
        data["j_hito_b_l"] = new("Index B Left");
        data["j_ko_a_l"] = new("Pinky A Left");
        data["j_ko_b_l"] = new("Pinky B Left");
        data["j_kusu_a_l"] = new("Ring A Left");
        data["j_kusu_b_l"] = new("Ring B Left");
        data["j_naka_a_l"] = new("Middle A Left");
        data["j_naka_b_l"] = new("Middle B Left");
        data["j_oya_a_l"] = new("Thumb A Left");
        data["j_oya_b_l"] = new("Thumb B Left");
    }
}
