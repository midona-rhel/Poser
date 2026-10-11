using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class RightArmBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_ude_a_r"] = new("Arm Right");
        data["j_ude_b_r"] = new("Forearm Right");
        data["n_hkata_r"] = new("Shoulder Right");
        data["n_hhiji_r"] = new("Elbow Right");
        data["n_hijisoubi_r"] = new("Couter Right");
        data["n_kataarmor_r"] = new("Pauldron Right");
    }
}
