using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class HeadBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_kao"] = new("Head", BoneCategory.Head);
    }
}
