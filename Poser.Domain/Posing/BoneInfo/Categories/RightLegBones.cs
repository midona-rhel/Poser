using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class RightLegBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_asi_a_r"] = new("Leg Right", BoneCategory.RightLeg);
        data["j_asi_b_r"] = new("Knee Right", BoneCategory.RightLeg);
        data["j_asi_c_r"] = new("Calf Right", BoneCategory.RightLeg);
        data["j_asi_d_r"] = new("Foot Right", BoneCategory.RightLeg);
        data["j_asi_e_r"] = new("Toes Right", BoneCategory.RightLeg);
        data["n_hizasoubi_r"] = new("Poleyn Right", BoneCategory.Equipment);
    }
}
