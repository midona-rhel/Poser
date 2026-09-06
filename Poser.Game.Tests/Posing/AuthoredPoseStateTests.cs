using System.Numerics;
using System.Reflection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Tests.Posing;

public sealed class AuthoredPoseStateTests
{
    [Fact]
    public void History_replays_only_authored_deltas_without_reading_an_animation_frame()
    {
        var (skeleton, arm, untouched, posing) = Create();
        var runtime = (PosingProxy)(object)posing;
        var rotation = new BonePoseTransformInfo(TransformComponents.All,
            new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), Vector3.Zero));
        runtime.Stacks[arm] = [rotation];
        var snapshot = AuthoredPoseState.Capture([skeleton], posing);
        Assert.Equal("arm", Assert.Single(snapshot.Bones).Name);
        runtime.Stacks[arm] = [];
        runtime.Stacks[untouched] = [rotation];
        for (int cycle = 0; cycle < 4; cycle++)
        {
            snapshot.Restore([skeleton], posing);
            Assert.Equal(rotation, Assert.Single(runtime.Stacks[arm]));
            Assert.Empty(runtime.Stacks[untouched]);
            snapshot = AuthoredPoseState.Capture([skeleton], posing);
        }
        // The bone proxy throws for LastTransform / LastRawTransform. No frame is baked.
    }

    [Fact]
    public void Partial_root_override_is_copied_and_missing_bones_are_skipped()
    {
        var (skeleton, arm, _, posing) = Create();
        arm.PartialRootScale = new Vector3(1.2f);
        var snapshot = AuthoredPoseState.Capture([skeleton], posing);
        arm.PartialRootScale = null;
        snapshot.Restore([skeleton], posing);
        Assert.Equal(new Vector3(1.2f), arm.PartialRootScale);
        snapshot.Restore([], posing);
    }

    [Fact]
    public void Continuously_owned_named_layers_are_not_captured_as_manual_edits()
    {
        var (skeleton, arm, _, posing) = Create();
        ((PosingProxy)(object)posing).Stacks[arm] =
            [new(TransformComponents.All, Transform.Zero, "expression")];
        Assert.Empty(AuthoredPoseState.Capture([skeleton], posing).Bones);
    }

    private static (ISkeleton, IBone, IBone, IBonePosingService) Create()
    {
        var skeleton = DispatchProxy.Create<ISkeleton, SkeletonProxy>();
        var arm = Bone("arm");
        var untouched = Bone("untouched");
        ((SkeletonProxy)(object)skeleton).Bones = [arm, untouched];
        return (skeleton, arm, untouched, DispatchProxy.Create<IBonePosingService, PosingProxy>());
    }

    private static IBone Bone(string name)
    {
        var bone = DispatchProxy.Create<IBone, BoneProxy>();
        ((BoneProxy)(object)bone).Name = name;
        return bone;
    }

    private class SkeletonProxy : DispatchProxy
    {
        public IReadOnlyList<IBone> Bones = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Slot" => PoseSlot.Character,
            "get_Bones" => Bones,
            _ => throw new InvalidOperationException(method.Name),
        };
    }

    private class BoneProxy : DispatchProxy
    {
        public string Name = "";
        public Vector3? Scale;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_BoneName": return Name;
                case "get_PartialId": return 0;
                case "get_PartialRootScale": return Scale;
                case "set_PartialRootScale": Scale = (Vector3?)args![0]; return null;
                default: throw new InvalidOperationException(method.Name);
            }
        }
    }

    private class PosingProxy : DispatchProxy
    {
        public Dictionary<IBone, IReadOnlyList<BonePoseTransformInfo>> Stacks = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var bone = (IBone)args![0]!;
            switch (method!.Name)
            {
                case "CapturePoseStacks": return Stacks.GetValueOrDefault(bone, []);
                case "RestorePoseStacks": Stacks[bone] = ((IReadOnlyList<BonePoseTransformInfo>)args[1]!).ToArray(); return null;
                default: throw new InvalidOperationException(method.Name);
            }
        }
    }
}
