using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class LeftLegBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_asi_a_l"] = new("Leg Left", BoneCategory.LeftLeg);
        data["j_asi_b_l"] = new("Knee Left", BoneCategory.LeftLeg);
        data["j_asi_c_l"] = new("Calf Left", BoneCategory.LeftLeg);
        data["j_asi_d_l"] = new("Foot Left", BoneCategory.LeftLeg);
        data["j_asi_e_l"] = new("Toes Left", BoneCategory.LeftLeg);
        data["n_hizasoubi_l"] = new("Poleyn Left", BoneCategory.Equipment);
    }
}
