using System.Numerics;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class IdentityAndSceneBaselineTests
{
    [Fact]
    public void Identity_is_generation_and_slot_safe_but_name_sensitive()
    {
        var actor = Actor();
        var character = new SkeletonId(actor, PoseSlot.Character, 0);
        var bone = new BoneId(character, 0, 4, "j_same");
        var renamed = new BoneId(character, 0, 4, "j_renamed");

        Assert.Equal(actor.LogicalId, actor.NextGeneration().LogicalId);
        Assert.NotEqual(character, character.NextGeneration());
        Assert.NotEqual(character, new SkeletonId(actor, PoseSlot.MainHand, 0));
        Assert.True(bone.IsValid);
        Assert.NotEqual(bone, renamed);
        Assert.Equal(bone.GetHashCode(), new BoneId(character, 0, 4, "j_same").GetHashCode());
        Assert.Equal(2, new Dictionary<BoneId, string> { [bone] = "a", [renamed] = "b" }.Count);
        Assert.False(new BoneId(new SkeletonId(actor, PoseSlot.Unknown, 0), 0, 0, "j").IsValid);
        Assert.False(new BoneId(character, -1, 0, "j").IsValid);
        Assert.False(new BoneId(character, 0, 0, " ").IsValid);
    }

    private static ActorId Actor() => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 0);
}
