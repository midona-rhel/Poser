using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class LeftArmBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_ude_a_l"] = new("Arm Left");
        data["j_ude_b_l"] = new("Forearm Left");
        data["n_hkata_l"] = new("Shoulder Left");
        data["n_hhiji_l"] = new("Elbow Left");
        data["n_hijisoubi_l"] = new("Couter Left");
        data["n_kataarmor_l"] = new("Pauldron Left");
    }
}
