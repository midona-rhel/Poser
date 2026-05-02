using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class HairBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_kami_a"] = new("Hair A", BoneCategory.Head);
        data["j_kami_b"] = new("Hair B", BoneCategory.Head);
        data["j_kami_f_l"] = new("Hair Front Left", BoneCategory.Head);
        data["j_kami_f_r"] = new("Hair Front Right", BoneCategory.Head);

        // Hair extras (hairstyle specific)
        data["j_ex_h0005_ke_b"] = new("Hair 5 Back", BoneCategory.Head);
        data["j_ex_h0005_ke_f"] = new("Hair 5 Front", BoneCategory.Head);
        data["j_ex_h0005_ke_l"] = new("Hair 5 Left", BoneCategory.Head);
        data["j_ex_h0005_ke_r"] = new("Hair 5 Right", BoneCategory.Head);
    }
}
