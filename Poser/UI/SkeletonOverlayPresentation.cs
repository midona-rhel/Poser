using System.Collections.Generic;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>Session presentation mask for the skeleton overlay only.</summary>
internal static class SkeletonOverlayPresentation
{
    private static readonly HashSet<BoneId> Hidden = new();

    public static bool IsVisible(BoneId bone) => !Hidden.Contains(bone);

    public static bool AreVisible(IReadOnlyList<BoneId> bones)
    {
        foreach (var bone in bones)
            if (Hidden.Contains(bone))
                return false;
        return true;
    }

    public static void SetVisible(IReadOnlyList<BoneId> bones, bool visible)
    {
        foreach (var bone in bones)
        {
            if (visible)
                Hidden.Remove(bone);
            else
                Hidden.Add(bone);
        }
    }
}
