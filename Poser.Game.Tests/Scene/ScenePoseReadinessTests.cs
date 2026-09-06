using System.Reflection;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class ScenePoseReadinessTests
{
    [Fact]
    public void Weapon_only_actor_waits_for_its_character_skeleton()
    {
        var weapon = Skeleton(PoseSlot.MainHand, true);
        Assert.False(SceneRuntimeAdapter.HasCharacterSkeleton([weapon]));
        Assert.False(SceneRuntimeAdapter.HasCharacterSkeleton([weapon, Skeleton(PoseSlot.Character, false)]));
        Assert.True(SceneRuntimeAdapter.HasCharacterSkeleton([weapon, Skeleton(PoseSlot.Character, true)]));
    }

    [Fact]
    public void Embedded_pose_is_frozen_and_does_not_append_separate_history()
    {
        var options = SceneRuntimeAdapter.SceneImportOptions;
        Assert.True(options.SuppressHistory);
        Assert.True(options.FreezeOnImport);
        Assert.True(options.ApplyRotation && options.ApplyPosition && options.ApplyScale);
        Assert.False(options.ApplyModelTransform);
    }

    private static ISkeleton Skeleton(PoseSlot slot, bool ready)
    {
        var skeleton = DispatchProxy.Create<ISkeleton, SkeletonProxy>();
        var proxy = (SkeletonProxy)(object)skeleton;
        proxy.Slot = slot;
        if (ready) proxy.Root = DispatchProxy.Create<IBone, SkeletonProxy>();
        return skeleton;
    }

    public class SkeletonProxy : DispatchProxy
    {
        public PoseSlot Slot;
        public IBone? Root;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Slot" => Slot,
            "get_RootBone" => Root,
            "get_Bones" => Root is null ? Array.Empty<IBone>() : new[] { Root },
            _ => null,
        };
    }
}
