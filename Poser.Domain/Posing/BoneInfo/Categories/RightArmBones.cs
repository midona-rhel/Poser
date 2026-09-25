using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class RightArmBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_ude_a_r"] = new("Arm Right", BoneCategory.RightArm);
        data["j_ude_b_r"] = new("Forearm Right", BoneCategory.RightArm);
        data["n_hkata_r"] = new("Shoulder Right", BoneCategory.RightArm);
        data["n_hhiji_r"] = new("Elbow Right", BoneCategory.RightArm);
        data["n_hijisoubi_r"] = new("Couter Right", BoneCategory.Equipment);
        data["n_kataarmor_r"] = new("Pauldron Right", BoneCategory.Equipment);
    }
}
