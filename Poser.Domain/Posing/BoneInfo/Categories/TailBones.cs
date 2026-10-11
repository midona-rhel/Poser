using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class TailBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["n_sippo_a"] = new("Tail A");
        data["n_sippo_b"] = new("Tail B");
        data["n_sippo_c"] = new("Tail C");
        data["n_sippo_d"] = new("Tail D");
        data["n_sippo_e"] = new("Tail E");
    }
}
