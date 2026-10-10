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
    public void History_replays_only_authored_deltas_and_partial_root_scale()
    {
        var (skeleton, arm, untouched, posing) = Create();
        var runtime = (PosingProxy)(object)posing;
        var rotation = new BonePoseTransformInfo(TransformComponents.All,
            new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), Vector3.Zero));
        runtime.Stacks[arm] = [rotation];
        // A continuously owned named layer is not a manual edit.
        runtime.Stacks[untouched] = [new(TransformComponents.All, Transform.Zero, "expression")];
        arm.PartialRootScale = new Vector3(1.2f);
        var snapshot = AuthoredPoseState.Capture([skeleton], posing);
        Assert.Equal("arm", Assert.Single(snapshot.Bones).Name);
        runtime.Stacks[arm] = [];
        runtime.Stacks[untouched] = [rotation];
        arm.PartialRootScale = null;
        for (int cycle = 0; cycle < 4; cycle++)
        {
            snapshot.Restore([skeleton], posing);
            Assert.Equal(rotation, Assert.Single(runtime.Stacks[arm]));
            Assert.Empty(runtime.Stacks[untouched]);
            Assert.Equal(new Vector3(1.2f), arm.PartialRootScale);
            snapshot = AuthoredPoseState.Capture([skeleton], posing);
        }
        // The bone proxy throws for LastTransform / LastRawTransform. No frame is baked.
        snapshot.Restore([], posing);
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
