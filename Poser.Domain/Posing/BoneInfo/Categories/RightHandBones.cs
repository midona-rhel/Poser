using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class RightHandBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_te_r"] = new("Hand Right");
        data["n_hte_r"] = new("Wrist Right");

        // Fingers
        data["j_hito_a_r"] = new("Index A Right");
        data["j_hito_b_r"] = new("Index B Right");
        data["j_ko_a_r"] = new("Pinky A Right");
        data["j_ko_b_r"] = new("Pinky B Right");
        data["j_kusu_a_r"] = new("Ring A Right");
        data["j_kusu_b_r"] = new("Ring B Right");
        data["j_naka_a_r"] = new("Middle A Right");
        data["j_naka_b_r"] = new("Middle B Right");
        data["j_oya_a_r"] = new("Thumb A Right");
        data["j_oya_b_r"] = new("Thumb B Right");
    }
}
