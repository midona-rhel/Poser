using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class RightHandBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_te_r"] = new("Hand Right", BoneCategory.RightArm);
        data["n_hte_r"] = new("Wrist Right", BoneCategory.RightArm);

        // Fingers
        data["j_hito_a_r"] = new("Index A Right", BoneCategory.RightArm);
        data["j_hito_b_r"] = new("Index B Right", BoneCategory.RightArm);
        data["j_ko_a_r"] = new("Pinky A Right", BoneCategory.RightArm);
        data["j_ko_b_r"] = new("Pinky B Right", BoneCategory.RightArm);
        data["j_kusu_a_r"] = new("Ring A Right", BoneCategory.RightArm);
        data["j_kusu_b_r"] = new("Ring B Right", BoneCategory.RightArm);
        data["j_naka_a_r"] = new("Middle A Right", BoneCategory.RightArm);
        data["j_naka_b_r"] = new("Middle B Right", BoneCategory.RightArm);
        data["j_oya_a_r"] = new("Thumb A Right", BoneCategory.RightArm);
        data["j_oya_b_r"] = new("Thumb B Right", BoneCategory.RightArm);
    }
}
