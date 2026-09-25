using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class TailBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["n_sippo_a"] = new("Tail A", BoneCategory.Tail);
        data["n_sippo_b"] = new("Tail B", BoneCategory.Tail);
        data["n_sippo_c"] = new("Tail C", BoneCategory.Tail);
        data["n_sippo_d"] = new("Tail D", BoneCategory.Tail);
        data["n_sippo_e"] = new("Tail E", BoneCategory.Tail);
    }
}
