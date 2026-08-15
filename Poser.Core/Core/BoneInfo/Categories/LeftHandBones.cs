using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class LeftHandBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_te_l"] = new("Hand Left", BoneCategory.LeftArm);
        data["n_hte_l"] = new("Wrist Left", BoneCategory.LeftArm);

        // Fingers
        data["j_hito_a_l"] = new("Index A Left", BoneCategory.LeftArm);
        data["j_hito_b_l"] = new("Index B Left", BoneCategory.LeftArm);
        data["j_ko_a_l"] = new("Pinky A Left", BoneCategory.LeftArm);
        data["j_ko_b_l"] = new("Pinky B Left", BoneCategory.LeftArm);
        data["j_kusu_a_l"] = new("Ring A Left", BoneCategory.LeftArm);
        data["j_kusu_b_l"] = new("Ring B Left", BoneCategory.LeftArm);
        data["j_naka_a_l"] = new("Middle A Left", BoneCategory.LeftArm);
        data["j_naka_b_l"] = new("Middle B Left", BoneCategory.LeftArm);
        data["j_oya_a_l"] = new("Thumb A Left", BoneCategory.LeftArm);
        data["j_oya_b_l"] = new("Thumb B Left", BoneCategory.LeftArm);
    }
}
