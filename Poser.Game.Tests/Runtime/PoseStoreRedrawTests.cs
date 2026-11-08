using Poser.Domain.Posing;

namespace Poser.Game.Tests.Runtime;

/// <summary>
/// A redraw replaces the skeleton instance. Pose state remains keyed by actor,
/// slot, partial, and bone name so the next live instance receives it.
/// </summary>
public sealed class PoseStoreRedrawTests
{
    [Fact]
    public void Bone_stacks_inside_a_store_are_addressed_by_name()
    {
        var store = new SkeletonPoseInfo();

        var first = store.GetPoseInfo("j_kosi", 0);
        var again = store.GetPoseInfo("j_kosi", 0);
        var other = store.GetPoseInfo("j_sebo_a", 0);

        // No skeleton instance participates in the lookup.
        Assert.Same(first, again);
        Assert.NotSame(first, other);
    }
}
