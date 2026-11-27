using System.Collections.Generic;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// Which skeletons the apply pass has still to pose in the current frame, and which it
/// posed in the last one. Bones on a skeleton not posed yet have no edits from this frame,
/// so an IK chain that follows one reads it where the last pass left it. That place is only
/// up to date when the last pass posed the same build of that skeleton.
/// </summary>
internal sealed class UnposedSkeletons
{
    private readonly HashSet<SkeletonKey> _keys = new();
    private Dictionary<SkeletonKey, long> _posed = new();
    private Dictionary<SkeletonKey, long> _posedLastPass = new();

    /// <summary>The pass starts with these skeletons still to pose.</summary>
    public void Start(List<SkeletonKey> pass)
    {
        (_posedLastPass, _posed) = (_posed, _posedLastPass);
        _posed.Clear();
        _keys.Clear();
        foreach (var key in pass)
            _keys.Add(key);
    }

    /// <summary>The pass takes up this skeleton, whether or not it can pose it.</summary>
    public void Taken(SkeletonKey key) => _keys.Remove(key);

    /// <summary>The pass posed this build of the skeleton.</summary>
    public void Posed(SkeletonKey key, long buildRevision) => _posed[key] = buildRevision;

    public void End() => _keys.Clear();

    public bool Contains(ISkeleton skeleton) => _keys.Contains(SkeletonKey.Of(skeleton));

    /// <summary>Whether the last pass posed this build of the skeleton, so the place it
    /// left each bone in is up to date.</summary>
    public bool PosedLastPass(ISkeleton skeleton) =>
        _posedLastPass.TryGetValue(SkeletonKey.Of(skeleton), out var revision)
        && revision == skeleton.BuildRevision;
}
