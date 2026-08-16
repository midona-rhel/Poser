using Poser.Domain.Identity;

namespace Poser.ContractTests;

public sealed class StableIdentityHashContractTests
{
    [Fact]
    public void Generation_changes_never_resolve_through_identity_maps()
    {
        var actor = new ActorId(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), 4);
        var nextActor = actor.NextGeneration();
        var skeleton = new SkeletonId(actor, PoseSlot.Character, 7);
        var nextSkeleton = skeleton.NextGeneration();

        var actors = new Dictionary<ActorId, string> { [actor] = "current" };
        var skeletons = new Dictionary<SkeletonId, string>
            { [skeleton] = "current" };

        Assert.NotEqual(actor, nextActor);
        Assert.NotEqual(skeleton, nextSkeleton);
        Assert.False(actors.ContainsKey(nextActor));
        Assert.False(skeletons.ContainsKey(nextSkeleton));
        Assert.Equal("current", actors[actor]);
        Assert.Equal("current", skeletons[skeleton]);
    }

    [Fact]
    public void Bone_name_remains_an_exact_dictionary_identity_guard()
    {
        var skeleton = new SkeletonId(
            new ActorId(
                Guid.Parse("22222222-2222-2222-2222-222222222222"), 2),
            PoseSlot.Character,
            3);
        var current = new BoneId(skeleton, 0, 12, "j_current");
        var differentName = new BoneId(skeleton, 0, 12, "j_replaced");
        var selected = SelectionId.ForBone(current);
        var staleSelection = SelectionId.ForBone(differentName);
        var bones = new Dictionary<BoneId, string> { [current] = "current" };
        var selections = new Dictionary<SelectionId, string>
            { [selected] = "current" };

        Assert.NotEqual(current, differentName);
        Assert.NotEqual(selected, staleSelection);
        Assert.False(bones.ContainsKey(differentName));
        Assert.False(selections.ContainsKey(staleSelection));
        Assert.Equal("current", bones[current]);
        Assert.Equal("current", selections[selected]);
    }
}
