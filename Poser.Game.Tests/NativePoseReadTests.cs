using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using Poser.Game.Entities;
using Xunit;

namespace Poser.Game.Tests;

public unsafe class NativePoseReadTests
{
    [Fact]
    public void ReadingAnAlreadySyncedPoseDoesNotInvalidateItsLocalRepresentation()
    {
        var transforms = stackalloc hkQsTransformf[2];
        var flags = stackalloc uint[2];
        flags[0] = flags[1] = 0;
        transforms[1].Scale.X = .8f;
        transforms[1].Scale.Y = 1.6f;
        transforms[1].Scale.Z = 1;
        var pose = new hkaPose { ModelInSync = 1, LocalInSync = 1 };
        pose.ModelPose.Data = transforms;
        pose.ModelPose.Length = 2;
        pose.BoneFlags.Data = flags;
        pose.BoneFlags.Length = 2;

        for (var i = 0; i < 100; i++)
        {
            var read = NativePoseRead.Model(&pose, 1);
            Assert.Equal((nint)(transforms + 1), (nint)read);
            Assert.Equal(1.6f, read->Scale.Y);
        }
        Assert.Equal((byte)1, pose.LocalInSync);
        Assert.Equal((byte)1, pose.ModelInSync);
        Assert.Equal(0u, flags[0]);
        Assert.Equal(0u, flags[1]);
    }

    [Fact]
    public void InvalidIndicesAreRejectedBeforeAnyNativeCall()
    {
        var pose = new hkaPose();
        Assert.Equal(0, (nint)NativePoseRead.Model(null, 0));
        Assert.Equal(0, (nint)NativePoseRead.Model(&pose, -1));
        Assert.Equal(0, (nint)NativePoseRead.Model(&pose, 0));
    }
}
