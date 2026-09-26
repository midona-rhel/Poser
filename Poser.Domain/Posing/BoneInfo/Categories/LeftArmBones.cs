using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class LeftArmBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_ude_a_l"] = new("Arm Left", BoneCategory.LeftArm);
        data["j_ude_b_l"] = new("Forearm Left", BoneCategory.LeftArm);
        data["n_hkata_l"] = new("Shoulder Left", BoneCategory.LeftArm);
        data["n_hhiji_l"] = new("Elbow Left", BoneCategory.LeftArm);
        data["n_hijisoubi_l"] = new("Couter Left", BoneCategory.Equipment);
        data["n_kataarmor_l"] = new("Pauldron Left", BoneCategory.Equipment);
    }
}
