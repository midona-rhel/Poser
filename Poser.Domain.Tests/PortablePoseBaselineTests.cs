using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class PortablePoseBaselineTests
{
    [Fact]
    public void Structural_entries_are_ordered_and_duplicate_names_with_paths_are_preserved()
    {
        var first = Entry(
            "j_dup",
            ["root", "left", "j_dup"],
            nativeIndexHint: 10,
            position: 1);
        var second = Entry(
            "j_dup",
            ["root", "right", "j_dup"],
            nativeIndexHint: 11,
            position: 2);

        var pose = new PortablePose(new[] { first, second });

        Assert.Equal(2, pose.Entries.Count);
        Assert.Equal(first.Key, pose.Entries[0].Key);
        Assert.Equal(second.Key, pose.Entries[1].Key);
        Assert.Equal(10, pose.Entries[0].NativeIndexHint);
        Assert.Equal(11, pose.Entries[1].NativeIndexHint);
        Assert.False(pose.TryGet(first.Key.LegacyId, out _));
    }

    [Fact]
    public void Structural_matching_reports_ambiguous_and_unmatched_entries()
    {
        var ambiguous = LegacyEntry("j_dup", position: 1);
        var unmatched = Entry(
            "j_missing",
            ["root", "missing", "j_missing"],
            nativeIndexHint: 1,
            position: 2);
        var pose = new PortablePose(new[]
        {
            ambiguous,
            unmatched,
        });
        var targets = new[]
        {
            Target("j_dup", ["root", "left", "j_dup"], nativeIndex: 2),
            Target("j_dup", ["root", "right", "j_dup"], nativeIndex: 3),
        };

        var result = pose.Match(targets);

        Assert.False(result.Success);
        Assert.Single(result.Ambiguous);
        Assert.Single(result.Unmatched);
        Assert.Empty(result.Matches);
        Assert.Equal("j_dup", result.Ambiguous[0].Entry.Key.CanonicalName);
        Assert.Contains("j_missing", result.Unmatched[0].Detail);
    }

    [Fact]
    public void Legacy_name_broadcast_is_only_available_when_explicitly_requested()
    {
        var legacy = new PortablePose(new[]
        {
            LegacyEntry("j_dup", position: 4),
        });
        var targets = new[]
        {
            Target("j_dup", ["root", "left", "j_dup"], nativeIndex: 2),
            Target("j_dup", ["root", "right", "j_dup"], nativeIndex: 3),
        };

        var rejected = legacy.Match(targets);
        var broadcast = legacy.Match(
            targets,
            PortableLegacyMatchPolicy.BroadcastAmbiguous);

        Assert.False(rejected.Success);
        Assert.Single(rejected.Ambiguous);
        Assert.True(broadcast.Success);
        Assert.Equal(2, broadcast.Matches.Count);
        Assert.Single(broadcast.Broadcasted);
    }

    [Fact]
    public void Native_index_is_a_hint_and_never_structural_identity()
    {
        var entry = Entry(
            "j_hand_l",
            ["root", "arm", "j_hand_l"],
            nativeIndexHint: 999,
            position: 3);
        var target = Target(
            "j_hand_l",
            ["root", "arm", "j_hand_l"],
            nativeIndex: 1);

        var result = new PortablePose(new[] { entry }).Match([target]);

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Equal(target.Bone, result.Matches[0].Target.Bone);
    }

    [Fact]
    public void Legacy_adapter_reports_loss_instead_of_overwriting_or_broadcasting()
    {
        var targets = new[]
        {
            Target("j_dup", ["root", "left", "j_dup"], nativeIndex: 2),
            Target("j_dup", ["root", "right", "j_dup"], nativeIndex: 3),
        };
        var legacy = new[]
        {
            new LegacyPortableBoneEntry(
                PoseSlot.Character,
                "j_dup",
                new BonePose()),
        };

        var result = LegacyPortablePoseAdapter.TryAdapt(legacy, targets);

        Assert.False(result.Success);
        Assert.True(result.LossDetected);
        Assert.Null(result.Pose);
        Assert.Single(result.Ambiguous);
    }

    [Fact]
    public void Legacy_adapter_can_upgrade_a_unique_name_to_structural_identity()
    {
        var target = Target(
            "j_unique",
            ["root", "arm", "j_unique"],
            nativeIndex: 5);
        var legacy = new[]
        {
            new LegacyPortableBoneEntry(
                PoseSlot.Character,
                "j_unique",
                new BonePose()),
        };

        var result = LegacyPortablePoseAdapter.TryAdapt(legacy, [target]);

        Assert.True(result.Success);
        Assert.False(result.LossDetected);
        Assert.NotNull(result.Pose);
        Assert.Equal(target.Key, result.Pose!.Entries[0].Key);
    }

    [Fact]
    public void Legacy_constructor_remains_callable_but_rejects_exact_duplicate_keys()
    {
        var id = new PortableBoneId(PoseSlot.Character, 0, "j_dup");
        var entries = new[]
        {
            new PortableBonePose(id, new BonePose()),
            new PortableBonePose(id, new BonePose()),
        };

        Assert.Throws<ArgumentException>(() => new PortablePose(entries));
    }

    private static PortableBoneEntry Entry(
        string name,
        IReadOnlyList<string> path,
        int nativeIndexHint,
        float position) =>
        new(
            new PortableBoneKey(
                PoseSlot.Character,
                new PortablePartialKey(0),
                name,
                new BonePath(path)),
            new BonePose([
                new PoseLayer(
                    new PoseLayerId(PoseLayerKind.Manual, name),
                    TransformComponents.All,
                    new PoseDelta(
                        new System.Numerics.Vector3(position, 0, 0),
                        System.Numerics.Quaternion.Identity,
                        System.Numerics.Vector3.Zero))]),
            nativeIndexHint);

    private static PortableBoneEntry LegacyEntry(
        string name,
        float position) =>
        new(
            PortableBoneKey.Legacy(
                new PortableBoneId(PoseSlot.Character, 0, name)),
            new BonePose([
                new PoseLayer(
                    new PoseLayerId(PoseLayerKind.Manual, name),
                    TransformComponents.All,
                    new PoseDelta(
                        new System.Numerics.Vector3(position, 0, 0),
                        System.Numerics.Quaternion.Identity,
                        System.Numerics.Vector3.Zero))]),
            null);

    private static PortableBoneTarget Target(
        string name,
        IReadOnlyList<string> path,
        int nativeIndex)
    {
        var actor = new ActorId(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            0);
        var bone = new BoneId(
            new SkeletonId(actor, PoseSlot.Character, 0),
            0,
            nativeIndex,
            name);
        return PortableBoneTarget.From(
            bone,
            new BonePath(path),
            nativeIndex);
    }
}
