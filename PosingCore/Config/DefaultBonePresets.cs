using System.Collections.Generic;
using Poser.Core.BoneInfo;

namespace Poser.Config;

/// <summary>
/// The stock bone filters, built from Ktisis's category tree so they name
/// exactly the bones the sidebar files under those headings (asked
/// 2026-09-03). Body is the spine from the hips to the head with the
/// abdomen's muscle bones; Head is the whole head subtree; the paired
/// filters take both sides, and each takes the IVCS bones of its own
/// region — fingers with the hands, toes with the feet, the arm and thigh
/// muscles with the arms and legs — since a preset only ever shows the
/// bones an actor carries.
/// </summary>
public static class DefaultBonePresets
{
    /// <summary>Bumped when a stock list changes; the seed then replaces
    /// the stock presets by name.</summary>
    public const int Version = 2;

    public static IReadOnlyList<BoneVisibilityPreset> Build() =>
    [
        Preset("Body", Own("Spine"), ["j_kubi", "j_kao"], Own("CustomAbdomen")),
        Preset("Head", Subtree("Head")),
        Preset("Arms", Own("LeftArm"), Own("RightArm"), Own("CustomArms")),
        Preset("Hands", Own("LeftHand"), Own("LeftHandIvcs"), Own("RightHand"), Own("RightHandIvcs")),
        Preset("Legs", Own("LeftLeg"), Own("RightLeg"), Own("CustomLegs")),
        Preset("Feet", Own("LeftFoot"), Own("LeftFootIvcs"), Own("RightFoot"), Own("RightFootIvcs")),
        Preset("Breasts", Own("Breasts"), Own("BreastsIvcs")),
        Preset("Privates", Own("GenitalsIvcs"), Own("PenisIvcs"), Own("VaginaIvcs"), Own("BottomIvcs")),
    ];

    private static BoneVisibilityPreset Preset(string name, params IEnumerable<string>[] parts)
    {
        var bones = new List<string>();
        var seen = new HashSet<string>();
        foreach (var part in parts)
            foreach (var bone in part)
                if (seen.Add(bone))
                    bones.Add(bone);
        return new BoneVisibilityPreset { Name = name, Bones = bones };
    }

    /// <summary>A category's own bones, not its children's.</summary>
    private static IEnumerable<string> Own(string id) =>
        Find(id) is { } category ? category.Bones : [];

    /// <summary>A category's bones and every descendant's.</summary>
    private static IEnumerable<string> Subtree(string id)
    {
        if (Find(id) is not { } category)
            yield break;
        var stack = new Stack<KtisisBoneCategory>();
        stack.Push(category);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var bone in current.Bones)
                yield return bone;
            foreach (var child in current.Children)
                stack.Push(child);
        }
    }

    private static KtisisBoneCategory? Find(string id)
    {
        var stack = new Stack<KtisisBoneCategory>(KtisisBoneCategories.Roots);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Id == id)
                return current;
            foreach (var child in current.Children)
                stack.Push(child);
        }
        return null;
    }
}
