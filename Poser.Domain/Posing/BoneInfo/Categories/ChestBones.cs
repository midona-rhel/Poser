using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class ChestBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_mune_l"] = new("Breast Left", BoneCategory.Spine);
        data["j_mune_r"] = new("Breast Right", BoneCategory.Spine);
        data["j_sako_l"] = new("Clavicle Left", BoneCategory.Spine);
        data["j_sako_r"] = new("Clavicle Right", BoneCategory.Spine);
    }
}
