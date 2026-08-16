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

public sealed class PoseFileServicePersistenceTests
{
    [Fact]
    public void Import_and_export_refuse_invalid_numeric_documents_without_mutating_state()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, "{\"Bones\":{\"j_kao\":{\"Rotation\":\"NaN, 0, 0, 1\"}}}");
        var service = Service();
        Assert.Null(service.BuildImportPlan(
            new[] { Skeleton(Bone("j_kao", Transform.Identity)) }, file.Path));

        File.WriteAllText(file.Path, "old destination");
        var invalidBone = Bone("j_kao", new Transform(
            Vector3.Zero, new Quaternion(float.NaN, 0, 0, 1), Vector3.One));
        Assert.False(service.ExportPose(
            new[] { Skeleton(invalidBone) }, file.Path));
        Assert.Equal("old destination", File.ReadAllText(file.Path));
    }

    [Fact]
    public void Import_plan_normalizes_runtime_rotations_without_mutating_the_source_pose()
    {
        using var file = new TempFile();
        var sourceRotation = new Quaternion(0, 0, 0, 2);
        var pose = new PoseFile
        {
            Bones = new Dictionary<string, PoseFile.BoneData>
            {
                ["j_kao"] = new() { Rotation = sourceRotation, Scale = Vector3.One },
            },
            ModelDifference = new() { Rotation = sourceRotation, Scale = Vector3.Zero },
        };
        Assert.True(AtomicPoseFileStore.Default.Write(pose, file.Path).Succeeded);
        var actor = Substitute.For<IActor>();
        var skeleton = Skeleton(Bone("j_kao", Transform.Identity));
        skeleton.Actor.Returns(actor);
        var posing = Substitute.For<IPosingService>();
        posing.GetEffectiveTransform(actor).Returns(Transform.Identity);
        posing.GetOriginalTransform(actor).Returns(Transform.Identity);
        var service = new PoseFileService(Substitute.For<IPluginLog>(), posing);

        var plan = service.BuildImportPlan(new[] { skeleton }, file.Path,
            new PoseImportOptions
            {
                ApplyBody = true, ApplyFace = true, ApplyRotation = true,
                ApplyPosition = true, ApplyScale = true, ApplyModelTransform = true,
            });

        Assert.NotNull(plan);
        Assert.Equal(Quaternion.Identity, Assert.Single(plan!.Writes).File.Rotation);
        Assert.Equal(sourceRotation, pose.Bones["j_kao"].Rotation);
        Assert.Equal(Quaternion.Identity, plan.ModelTransform.Rotation);
        Assert.Equal(sourceRotation, pose.ModelDifference.Rotation);
    }

    private static PoseFileService Service() =>
        new(Substitute.For<IPluginLog>(), Substitute.For<IPosingService>());

    private static ISkeleton Skeleton(IBone bone)
    {
        var skeleton = Substitute.For<ISkeleton>();
        skeleton.Slot.Returns(PoseSlot.Character);
        skeleton.Bones.Returns(new[] { bone });
        skeleton.GetBone(bone.BoneName).Returns(bone);
        bone.Skeleton.Returns(skeleton);
        bone.ParentBone.Returns((IBone?)null);
        return skeleton;
    }

    private static IBone Bone(string name, Transform transform)
    {
        var bone = Substitute.For<IBone>();
        bone.BoneName.Returns(name);
        bone.LastRawTransform.Returns(transform);
        return bone;
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-service-{Guid.NewGuid():N}.pose");
        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
