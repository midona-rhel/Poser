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
