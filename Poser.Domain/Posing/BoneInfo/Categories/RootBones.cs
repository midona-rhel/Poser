using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class RootBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["n_root"] = new("Root");
        data["n_hara"] = new("Abdomen");
        data["n_throw"] = new("Throw");
    }
}
