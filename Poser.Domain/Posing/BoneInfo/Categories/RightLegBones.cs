using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class RightLegBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_asi_a_r"] = new("Leg Right");
        data["j_asi_b_r"] = new("Knee Right");
        data["j_asi_c_r"] = new("Calf Right");
        data["j_asi_d_r"] = new("Foot Right");
        data["j_asi_e_r"] = new("Toes Right");
        data["n_hizasoubi_r"] = new("Poleyn Right");
    }
}
