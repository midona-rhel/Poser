using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class HeadBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        data["j_kao"] = new("Head", BoneCategory.Head);
    }
}
