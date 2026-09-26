using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class RootBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["n_root"] = new("Root", BoneCategory.Root);
        data["n_hara"] = new("Abdomen", BoneCategory.Root);
        data["n_throw"] = new("Throw", BoneCategory.Root);
    }
}
