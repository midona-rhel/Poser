using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>
/// Session presentation mask for the skeleton overlay. Bones start HIDDEN:
/// the sidebar's Skeleton node (and the finer category/bone eyes) opt them
/// in — the replaced armature toggle's per-actor successor (user
/// 2026-08-11). Selection anchors bypass this mask at the overlay.
/// </summary>
public sealed class SkeletonOverlayPresentation
{
    private readonly HashSet<BoneId> _shown = new();

    /// <summary>Whether anything at all is opted in.</summary>
    public bool AnyVisible => _shown.Count > 0;

    public bool IsVisible(BoneId bone) => _shown.Contains(bone);

    public bool AreVisible(IReadOnlyList<BoneId> bones)
    {
        foreach (var bone in bones)
            if (!_shown.Contains(bone))
                return false;
        return bones.Count > 0;
    }

    public void SetVisible(IReadOnlyList<BoneId> bones, bool visible)
    {
        foreach (var bone in bones)
        {
            if (visible)
                _shown.Add(bone);
            else
                _shown.Remove(bone);
        }
    }

    public void Reconcile(SceneSnapshot snapshot)
    {
        if (_shown.Count == 0)
            return;
        var present = new HashSet<BoneId>();
        foreach (var actor in snapshot.Actors)
            foreach (var skeleton in actor.Skeletons)
                foreach (var bone in skeleton.Bones)
                    present.Add(bone.Id);
        _shown.RemoveWhere(bone => !present.Contains(bone));
    }

    public void Clear() => _shown.Clear();
}
