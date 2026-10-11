using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class LeftLegBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_asi_a_l"] = new("Leg Left");
        data["j_asi_b_l"] = new("Knee Left");
        data["j_asi_c_l"] = new("Calf Left");
        data["j_asi_d_l"] = new("Foot Left");
        data["j_asi_e_l"] = new("Toes Left");
        data["n_hizasoubi_l"] = new("Poleyn Left");
    }
}
