using Poser.Core;

namespace Poser.Game.Tests.LegacyRuntime;

/// <summary>
/// THE REDRAW CONTRACT, at the seam issue #78 moved it to: the pose store.
///
/// <para>A pose is addressed by (actor, slot) above the write layer and by
/// bone NAME inside that. A redraw builds a new skeleton instance; nothing
/// above the write layer may notice. The store keeps the pose exactly where it
/// was and the next apply pass lands it on whatever instance is live — no
/// parking lot, no adoption, no migration.</para>
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

        // Same name, same partial, same stack — no instance anywhere in the
        // lookup, so a rebuilt skeleton finds the pose it authored.
        Assert.Same(first, again);
        Assert.NotSame(first, other);
    }
}
