using System.Numerics;
using Poser.Domain.Collections;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Domain.Tests;

public sealed class SceneSnapshotEqualityTests
{
    private static readonly ActorId ActorA = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 0);
    private static readonly ActorId ActorB = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 0);
    private static readonly SkeletonId Skeleton = new(ActorA, PoseSlot.Character, 0);
    private static readonly BoneId Root = new(Skeleton, 0, 0, "n_root");
    private static readonly BoneId Hand = new(Skeleton, 0, 5, "j_hand_l");
    private static readonly LightId Light = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 0);
    private static readonly CameraId Camera = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), 0);
    private static readonly PropId Prop = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0);
    private static readonly WorldObjectId WorldObject = new(Guid.Parse("44444444-4444-4444-4444-444444444444"), 0);
    private static readonly OverlayId Overlay = new(Guid.Parse("55555555-5555-5555-5555-555555555555"), 0);

    // Built fresh on every call, so two calls share no list instance.
    private static SceneSnapshot Baseline() => new(
        7,
        [new ActorDescriptor(
            ActorA,
            "Actor",
            [new SkeletonDescriptor(
                Skeleton,
                [
                    new BoneDescriptor(Root, "Root", null),
                    new BoneDescriptor(Hand, "Hand", Root),
                ])],
            OwnerActor: ActorB,
            AttachmentKind: CompanionKind.Companion)],
        [new LightDescriptor(Light, "Light", LightKind.Point)],
        [new CameraDescriptor(Camera, "Camera", CameraKind.Free)],
        [new PropDescriptor(Prop, "Prop")],
        new EnvironmentDescriptor(600, 12, 2),
        [new GazeDescriptor(ActorA)],
        [new OverlayDescriptor(Overlay, "Overlay", OverlayNodeKind.Talk)],
        [new WorldObjectDescriptor(WorldObject, "Tree", "bg/tree.sgb")]);

    private static SceneSnapshot Actor(SceneSnapshot s, Func<ActorDescriptor, ActorDescriptor> edit) =>
        s with { Actors = [edit(s.Actors[0])] };

    private static SceneSnapshot Gaze(SceneSnapshot s, Func<GazeDescriptor, GazeDescriptor> edit) =>
        s with { GazeStates = [edit(s.GazeStates[0])] };

    private static SceneSnapshot World(SceneSnapshot s, Func<WorldObjectDescriptor, WorldObjectDescriptor> edit) =>
        s with { WorldObjects = [edit(s.WorldObjects[0])] };

    [Theory]
    [InlineData("ActorDescriptor.IsAdopted")]
    [InlineData("WorldObjectDescriptor.VfxPaused")]
    public void Toggling_a_runtime_flag_is_a_content_change(string field)
    {
        var edited = field == "ActorDescriptor.IsAdopted"
            ? Actor(Baseline(), a => a with { IsAdopted = true })
            : World(Baseline(), w => w with { VfxPaused = true });

        Assert.False(Baseline().ContentEquals(edited));
        Assert.False(edited.ContentEquals(Baseline()));
    }

    [Fact]
    public void Equal_content_in_different_list_instances_is_equal()
    {
        var first = Baseline();
        var second = Baseline();

        Assert.NotSame(first.Actors, second.Actors);
        Assert.NotSame(first.Actors[0].Skeletons, second.Actors[0].Skeletons);
        Assert.NotSame(first.Actors[0].Skeletons[0].Bones, second.Actors[0].Skeletons[0].Bones);
        Assert.True(first.ContentEquals(second));
        Assert.True(second.ContentEquals(first));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        var bones = new List<BoneDescriptor> { new(Root, "Root", null) };
        var fromList = new SkeletonDescriptor(Skeleton, bones);
        var fromArray = new SkeletonDescriptor(Skeleton, bones.ToArray());
        bones.Clear();
        Assert.Equal(fromList, fromArray);
        Assert.Single(fromList.Bones);
    }

    [Fact]
    public void Content_equality_is_ordinal_and_exact()
    {
        var baseline = Baseline();

        Assert.False(baseline.ContentEquals(World(Baseline(), w => w with { Name = "TREE" })));
        Assert.False(baseline.ContentEquals(
            Gaze(Baseline(), g => g with { Anchor = new Vector3(1e-7f, 0, 0) })));
        Assert.False(baseline.ContentEquals(null));
    }

    [Fact]
    public void Value_list_is_structural()
    {
        var left = ValueList.From(new[] { 1, 2, 3 });
        var right = ValueList.From(new List<int> { 1, 2, 3 });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, ValueList.From(new[] { 1, 2 }));
        Assert.NotEqual(left, ValueList.From(new[] { 1, 3, 2 }));
    }
}
