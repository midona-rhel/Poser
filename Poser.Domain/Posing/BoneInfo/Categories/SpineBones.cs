using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class SpineBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_kosi"] = new("Waist");
        data["j_sebo_a"] = new("Lumbar");
        data["j_sebo_b"] = new("Thoracic");
        data["j_sebo_c"] = new("Cervical");
        data["j_kubi"] = new("Neck");
    }
}
