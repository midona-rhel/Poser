using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

/// <summary>
/// The legacy .cmp path's two DELIBERATE deviations from Brio, pinned here so
/// no later audit reads them as accidental drift. Both are named in
/// <c>docs/brio/known-brio-bugs.md</c>.
/// </summary>
public class CmpImportDeviationTests
{
    /// <summary>Hex-encoded 1.0f — the .cmp wire format's little-endian float
    /// bytes, space separated.</summary>
    private const string OneFloat = "00 00 80 3F";

    private static string WriteCmp(string body)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"poser-cmp-{Guid.NewGuid():N}.cmp");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>
    /// DELIBERATE DEVIATION: a .cmp bone that carries a scale but no rotation
    /// gets the IDENTITY quaternion here, where Brio's
    /// <c>StringToBone</c> leaves <c>PoseFile.Bone.Rotation</c> at
    /// <c>default(Quaternion)</c> — (0,0,0,0), which is not a rotation at all
    /// (CMToolPoseFile.cs:572-599). Applying that zero quaternion collapses
    /// the bone.
    /// </summary>
    [Fact]
    public void ScaleOnlyCmpBoneGetsIdentityRotationNotZero()
    {
        var path = WriteCmp(
            $$"""
            { "Race": "1", "WaistSize": "{{OneFloat}} {{OneFloat}} {{OneFloat}}" }
            """);
        try
        {
            var upgraded = CMToolPoseFile.Load(path)!.Upgrade();

            var bone = Assert.Single(upgraded.Bones).Value;
            Assert.Equal(Quaternion.Identity, bone.Rotation);
            Assert.NotEqual(default, bone.Rotation);
            Assert.Equal(Vector3.One, bone.Scale);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// DELIBERATE DEVIATION: the .cmp format carries NO positions, so every
    /// upgraded bone's position is a structural zero. Brio applies those zeros
    /// whenever the popup's Position toggle happens to be on; Poser clamps the
    /// component mask to Rotation | Scale for the whole .cmp path
    /// (PoseFileService.BuildImportPlan's <c>maskLimit</c>), so a .cmp can
    /// never teleport a bone to the origin no matter what the options say.
    /// </summary>
    [Fact]
    public void CmpImportNeverCarriesPositionEvenWhenTheOptionsAskForIt()
    {
        var path = WriteCmp(
            $$"""
            { "Race": "1", "Waist": "{{OneFloat}} {{OneFloat}} {{OneFloat}} {{OneFloat}}" }
            """);
        try
        {
            var skeleton = SkeletonWith("j_kosi");
            var service = new PoseFileService(
                Substitute.For<IPluginLog>(), Substitute.For<IPosingService>());

            var plan = service.BuildImportPlan(
                new[] { skeleton },
                path,
                new PoseImportOptions
                {
                    ApplyRotation = true,
                    ApplyPosition = true,
                    ApplyScale = true,
                    ApplyBody = true,
                    ApplyFace = true,
                });

            Assert.NotNull(plan);
            var write = Assert.Single(plan!.Writes);
            Assert.False(write.Components.HasFlag(TransformComponents.Position));
            Assert.True(write.Components.HasFlag(TransformComponents.Rotation));
            Assert.True(write.Components.HasFlag(TransformComponents.Scale));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A Character skeleton carrying exactly the named bones.</summary>
    private static ISkeleton SkeletonWith(params string[] boneNames)
    {
        var skeleton = Substitute.For<ISkeleton>();
        var bones = new List<IBone>();
        foreach (var name in boneNames)
        {
            var bone = Substitute.For<IBone>();
            bone.BoneName.Returns(name);
            bone.Skeleton.Returns(skeleton);
            bone.ParentBone.Returns((IBone?)null);
            bone.LastRawTransform.Returns(Transform.Identity);
            bones.Add(bone);
            skeleton.GetBone(name).Returns(bone);
        }
        skeleton.Slot.Returns(PoseSlot.Character);
        skeleton.Bones.Returns(bones);
        return skeleton;
    }
}
