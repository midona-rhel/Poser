using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class ChestBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_mune_l"] = new("Breast Left");
        data["j_mune_r"] = new("Breast Right");
        data["j_sako_l"] = new("Clavicle Left");
        data["j_sako_r"] = new("Clavicle Right");
    }
}
