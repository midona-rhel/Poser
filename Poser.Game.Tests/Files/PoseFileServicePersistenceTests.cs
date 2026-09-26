using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

public sealed class PoseFileServicePersistenceTests
{
    [Fact]
    public void Legacy_cmp_import_never_applies_positions_even_when_requested()
    {
        using var file = new TempFile(".cmp");
        File.WriteAllText(file.Path, """
            {"Race":"1","Head":"00 00 00 00 00 00 00 00 00 00 00 00 00 00 80 3F",
             "HeadSize":"00 00 80 3F 00 00 80 3F 00 00 80 3F"}
            """);
        var plan = Service().BuildImportPlan(new[] { Skeleton(Bone("j_kao", Transform.Identity)) },
            file.Path, new PoseImportOptions { ApplyPosition = true, ApplyRotation = true, ApplyScale = true, ApplyFace = true });
        Assert.NotNull(plan);
        var write = Assert.Single(plan.Writes);
        Assert.Equal(TransformComponents.Rotation | TransformComponents.Scale, write.Components);
        Assert.Equal(Quaternion.Identity, write.File.Rotation);
        Assert.Equal(Vector3.One, write.File.Scale);
    }

    [Fact]
    public void Empty_legacy_bone_rotation_never_becomes_a_native_pose_write()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, """{"Bones":{"Head":{"Rotation":"1E-45, -1E-45, 0, 0","Scale":"0, 0, 0"}}}""");
        var plan = Service().BuildImportPlan(new[] { Skeleton(Bone("j_kao", Transform.Identity)) }, file.Path);
        Assert.NotNull(plan);
        Assert.Empty(plan.Writes);
    }

    [Theory]
    [InlineData(PoseSlot.Character, false)]
    [InlineData(PoseSlot.Character, true)]
    [InlineData(PoseSlot.MainHand, false)]
    [InlineData(PoseSlot.MainHand, true)]
    public void Captured_chain_keeps_requested_positions_even_when_local_offsets_match(
        PoseSlot slot, bool reset)
    {
        var skeleton = Substitute.For<ISkeleton>();
        skeleton.Slot.Returns(slot);
        var pose = new PoseFile();
        var collection = slot == PoseSlot.Character ? pose.Bones : pose.MainHand;
        var bones = new List<IBone>();
        // The reported FABRIK chain has 23 links. Capture the already-posed
        // chain, as reset/reapply does; all local offsets match at plan time.
        for (int i = 0; i < 24; i++)
        {
            var transform = new Transform(
                new Vector3(0, 1 + 0.2f * MathF.Sin(i * 0.15f), i * 0.04f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, i * 0.15f),
                Vector3.One);
            var bone = Bone($"nf_leash_chain_{20 + i}", transform);
            bone.Skeleton.Returns(skeleton);
            bone.ParentBone.Returns(i == 0 ? null : bones[i - 1]);
            skeleton.GetBone(bone.BoneName).Returns(bone);
            bones.Add(bone);
            collection[bone.BoneName] = transform;
        }
        skeleton.Bones.Returns(bones);
        var options = new PoseImportOptions
        {
            ApplyPosition = true, ApplyScale = true, ResetBeforeImport = reset,
        };
        var plan = Service().BuildImportPlan(new[] { skeleton }, pose, options);

        Assert.Equal(24, plan.Writes.Count);
        Assert.All(plan.Writes, write =>
        {
            Assert.Equal(TransformComponents.All, write.Components);
            Assert.Equal(collection[write.Bone].Position, write.File.Position);
        });

        // A user disabling positions still gets rotation/scale only.
        options.ApplyPosition = false;
        var rotationPlan = Service().BuildImportPlan(new[] { skeleton }, pose, options);
        Assert.Equal(24, rotationPlan.Writes.Count);
        Assert.All(rotationPlan.Writes, write =>
            Assert.False(write.Components.HasFlag(TransformComponents.Position)));
    }

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

    [Fact]
    public void ExportPose_moves_then_replaces_atomically_without_recovery_files()
    {
        using var file = new TempFile();
        var service = Service();
        var skeletons = new[] { Skeleton(Bone("j_kao", Transform.Identity)) };

        Assert.True(service.ExportPose(skeletons, file.Path));
        var first = File.ReadAllBytes(file.Path);
        Assert.NotEmpty(first);

        Assert.True(service.ExportPose(skeletons, file.Path));
        Assert.Equal(first, File.ReadAllBytes(file.Path));
        Assert.True(AtomicPoseFileStore.Default.Read(file.Path).Succeeded);

        var directory = System.IO.Path.GetDirectoryName(file.Path)!;
        var name = System.IO.Path.GetFileName(file.Path);
        Assert.Empty(Directory.GetFiles(directory, $".{name}.*"));
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
        public string Path { get; }
        public TempFile(string extension = ".pose") => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-service-{Guid.NewGuid():N}{extension}");
        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
