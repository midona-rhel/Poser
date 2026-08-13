using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.ContractTests;

public sealed class PortablePoseApplicationContractTests
{
    [Fact]
    public void CapturePortable_preserves_ordered_duplicate_name_paths()
    {
        var (left, right) = DuplicateBones();
        using var app = Harness(left, right);

        var captured = app.PoseEdits.CapturePortable(
            [TransformTargetId.ForBone(left), TransformTargetId.ForBone(right)]);

        Assert.True(captured.Success, captured.Detail);
        Assert.NotNull(captured.Pose);
        Assert.Equal(2, captured.Pose!.Entries.Count);
        Assert.Equal(
            new BonePath("root", "left", "j_dup"),
            captured.Pose.Entries[0].Key.Path);
        Assert.Equal(
            new BonePath("root", "right", "j_dup"),
            captured.Pose.Entries[1].Key.Path);
        Assert.Equal(left.BoneIndex, captured.Pose.Entries[0].NativeIndexHint);
        Assert.Equal(right.BoneIndex, captured.Pose.Entries[1].NativeIndexHint);
        Assert.False(captured.Pose.TryGet(captured.Pose.Entries[0].LegacyId, out _));
    }

    [Fact]
    public void ApplyPortable_matches_structural_path_when_native_index_changes()
    {
        var (left, right) = DuplicateBones();
        using var app = Harness(left, right);
        var target = TransformTargetId.ForBone(right);
        var pose = new PortablePose([
            new PortableBoneEntry(
                new PortableBoneKey(
                    PoseSlot.Character,
                    right.PartialId,
                    right.CanonicalName,
                    new BonePath("root", "right", "j_dup")),
                PoseAt(8),
                NativeIndexHint: 999),
        ]);

        var result = app.PoseEdits.ApplyPortable([target], pose, "structural apply");

        Assert.True(result.Success, result.Detail);
        Assert.Equal(1, result.Affected);
        Assert.Single(app.Runtime.RestoreCalls);
        Assert.Equal(target, app.Runtime.RestoreCalls[0]);
        Assert.Equal(8, app.Runtime.State(target).Pose.Layers[0].Delta.Position.X);
    }

    [Fact]
    public void ApplyPortable_reports_legacy_ambiguity_without_broadcasting()
    {
        var (left, right) = DuplicateBones();
        using var app = Harness(left, right);
        var pose = new PortablePose([
            new PortableBonePose(
                new PortableBoneId(PoseSlot.Character, 0, "j_dup"),
                PoseAt(8)),
        ]);

        var result = app.PoseEdits.ApplyPortable(
            [TransformTargetId.ForBone(left), TransformTargetId.ForBone(right)],
            pose,
            "ambiguous legacy apply");

        Assert.False(result.Success);
        Assert.Contains("Ambiguous", result.Detail!);
        Assert.Empty(app.Runtime.RestoreCalls);
    }

    private static TransformApplicationHarness Harness(params BoneId[] bones)
    {
        var app = new TransformApplicationHarness();
        var actor = TestIds.Actor();
        app.Scene.Refresh(new SceneSnapshot(
            1,
                [new ActorDescriptor(
                actor,
                "Test actor",
                [new SkeletonDescriptor(
                    bones[0].Skeleton,
                    [
                        new BoneDescriptor(
                            new BoneId(bones[0].Skeleton, 0, 0, "root"),
                            "root",
                            null),
                        new BoneDescriptor(
                            new BoneId(bones[0].Skeleton, 0, 1, "left"),
                            "left",
                            new BoneId(bones[0].Skeleton, 0, 0, "root")),
                        new BoneDescriptor(
                            new BoneId(bones[0].Skeleton, 0, 2, "right"),
                            "right",
                            new BoneId(bones[0].Skeleton, 0, 0, "root")),
                        new BoneDescriptor(
                            bones[0],
                            bones[0].CanonicalName,
                            new BoneId(bones[0].Skeleton, 0, 1, "left")),
                        new BoneDescriptor(
                            bones[1],
                            bones[1].CanonicalName,
                            new BoneId(bones[0].Skeleton, 0, 2, "right")),
                    ])])],
            [],
            [],
            []));
        foreach (var bone in bones)
        {
            var target = TransformTargetId.ForBone(bone);
            app.Runtime.Seed(TestStates.At(target, 0, hasOverride: false));
        }

        return app;
    }

    private static (BoneId Left, BoneId Right) DuplicateBones()
    {
        var skeleton = new SkeletonId(TestIds.Actor(), PoseSlot.Character, 0);
        return (
            new BoneId(skeleton, 0, 10, "j_dup"),
            new BoneId(skeleton, 0, 11, "j_dup"));
    }

    private static BonePose PoseAt(float x) =>
        new([
            new PoseLayer(
                new PoseLayerId(PoseLayerKind.Manual, "portable"),
                TransformComponents.All,
                new PoseDelta(
                    new Vector3(x, 0, 0),
                    Quaternion.Identity,
                    Vector3.Zero)),
        ]);
}
