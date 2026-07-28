using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>Session presentation mask for the skeleton overlay only.</summary>
public sealed class SkeletonOverlayPresentation
{
    private readonly HashSet<BoneId> _hidden = new();

    public bool IsVisible(BoneId bone) => !_hidden.Contains(bone);

    public bool AreVisible(IReadOnlyList<BoneId> bones)
    {
        foreach (var bone in bones)
            if (_hidden.Contains(bone))
                return false;
        return true;
    }

    public void SetVisible(IReadOnlyList<BoneId> bones, bool visible)
    {
        foreach (var bone in bones)
        {
            if (visible)
                _hidden.Remove(bone);
            else
                _hidden.Add(bone);
        }
    }

    public void Reconcile(SceneSnapshot snapshot)
    {
        if (_hidden.Count == 0)
            return;
        var present = new HashSet<BoneId>();
        foreach (var actor in snapshot.Actors)
            foreach (var skeleton in actor.Skeletons)
                foreach (var bone in skeleton.Bones)
                    present.Add(bone.Id);
        _hidden.RemoveWhere(bone => !present.Contains(bone));
    }

    public void Clear() => _hidden.Clear();
}
