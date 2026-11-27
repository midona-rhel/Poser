using System.Collections.Generic;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// The skeletons the apply pass has still to pose in the current frame. Their bones
/// do not have this frame's edits yet, so an IK chain that follows a bone on one of
/// them reads it where it was shown at the end of the last frame. The pass fills this
/// when it starts and takes each skeleton out as it begins to pose it.
/// </summary>
internal sealed class UnposedSkeletons
{
    private readonly HashSet<SkeletonKey> _keys = new();

    public void Start(List<SkeletonKey> pass)
    {
        _keys.Clear();
        foreach (var key in pass)
            _keys.Add(key);
    }

    public void Posing(SkeletonKey key) => _keys.Remove(key);

    public void End() => _keys.Clear();

    public bool Contains(ISkeleton skeleton) => _keys.Contains(SkeletonKey.Of(skeleton));
}
