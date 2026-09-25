using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class SpineBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_kosi"] = new("Waist", BoneCategory.Spine);
        data["j_sebo_a"] = new("Lumbar", BoneCategory.Spine);
        data["j_sebo_b"] = new("Thoracic", BoneCategory.Spine);
        data["j_sebo_c"] = new("Cervical", BoneCategory.Spine);
        data["j_kubi"] = new("Neck", BoneCategory.Spine);
    }
}
