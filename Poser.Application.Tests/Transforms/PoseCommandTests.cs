using System.Numerics;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class PoseCommandTests
{
    [Fact]
    public void Region_reset_leaves_auxiliary_pose_and_actor_placement_alone_and_undo_restores_edits()
    {
        using var f = new Fixture();
        var original = f.Live.ToDictionary();
        Assert.True(f.Commands.Reset(f.Actor, PoseRegion.Body).Success);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Equal(original[f.Weapon], f.Live[f.Weapon]);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
        Assert.True(f.Gestures.Undo().Success);
        Assert.Equal(original[f.Character], f.Live[f.Character]);
        Assert.True(f.Commands.Reset(f.Actor, PoseRegion.All).Success);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Empty(f.Live[f.Weapon].Pose.Layers);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
    }

    [Fact]
    public void Stash_restores_all_bone_slots_without_model_transform_while_mirror_includes_facing()
    {
        using var f = new Fixture();
        var original = f.Live.ToDictionary();
        Assert.True(f.Commands.Stash(f.Actor, "source").Success);
        Assert.True(f.Commands.Reset(f.Actor, PoseRegion.All).Success);
        Assert.True(f.Commands.ApplyStash(f.Actor).Success);
        Assert.Equal(original[f.Character].Pose.Evaluate(), f.Live[f.Character].Pose.Evaluate());
        Assert.Equal(original[f.Weapon].Pose.Evaluate(), f.Live[f.Weapon].Pose.Evaluate());
        Assert.Equal(original[f.Model], f.Live[f.Model]);
        Assert.True(f.Commands.Mirror(f.Actor).Success);
        var mirror = Assert.IsType<TransformPatch>(f.History.PeekUndo());
        Assert.Contains(mirror.After, state => state.Target == f.Model);
        Assert.Equal(original[f.Model].Transform.Position, f.Live[f.Model].Transform.Position);
        Assert.True(f.Gestures.Undo().Success);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
    }

    [Fact]
    public void Old_actor_and_bone_commands_do_not_redirect_to_new_generation_or_overwrite_stash()
    {
        using var f = new Fixture();
        Assert.True(f.Commands.Stash(f.Actor, "kept").Success);
        var replacement = f.Actor with { Generation = f.Actor.Generation + 1 };
        Assert.True(f.Scene.TryRefresh(new SceneSnapshot(2,
            [new ActorDescriptor(replacement, "replacement", [])], [], [], [])).Accepted);
        f.Captures = 0;
        Assert.False(f.Commands.Reset(f.Actor, PoseRegion.All).Success);
        Assert.False(f.Commands.Mirror(f.Actor).Success);
        Assert.False(f.Commands.Stash(f.Actor, "lost").Success);
        Assert.False(f.Commands.ApplyStash(f.Actor).Success);
        Assert.False(f.Commands.ResetBone(f.Character, "old bone").Success);
        Assert.False(f.Commands.HasAuthoredEdits(f.Actor));
        Assert.Equal("kept", f.Commands.StashedFrom);
        Assert.Equal(0, f.Captures);
        Assert.False(f.History.CanUndo);
    }

    private sealed class Fixture : ITransformRuntimePort, IPoseEditReads, IDisposable
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly SceneSession Scene = new(new SelectionSession());
        public readonly TransformHistory History = new();
        public readonly Dictionary<TransformTargetId, TransformTargetState> Live = new();
        public readonly TransformTargetId Character, Weapon, Model;
        public readonly TransformGestureService Gestures;
        public readonly IPoseCommands Commands;
        public int Captures;

        public Fixture()
        {
            var body = new BoneId(new SkeletonId(Actor, PoseSlot.Character, 0), 0, 0, "j_ude_a_l");
            var weapon = new BoneId(new SkeletonId(Actor, PoseSlot.MainHand, 0), 0, 0, "j_ude_a_l");
            Character = TransformTargetId.ForBone(body);
            Weapon = TransformTargetId.ForBone(weapon);
            Model = TransformTargetId.ForActor(Actor);
            Assert.True(Scene.TryRefresh(new SceneSnapshot(1,
                [new ActorDescriptor(Actor, "actor", [
                    new(body.Skeleton, [new(body, "arm", null)]),
                    new(weapon.Skeleton, [new(weapon, "weapon", null)])])], [], [], [])).Accepted);
            foreach (var target in new[] { Character, Weapon, Model })
                Live[target] = new(target,
                    new(new Vector3(4, 5, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), Vector3.One),
                    new BonePose([new(new(PoseLayerKind.Manual, "manual"), TransformComponents.All,
                        new(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), Vector3.Zero))]), true);
            Gestures = new(Scene, this, History);
            var edits = new PoseEditService(Scene, this, History, Gestures);
            Commands = new PoseCommands(Scene, edits, new(edits), this);
        }

        public bool HasAuthoredEdits(ActorId actor) => true;
        public TransformPortResult Capture(TransformTargetId target)
        {
            Captures++;
            return TransformPortResult.Ok(Live[target]);
        }
        public TransformPortResult Restore(TransformTargetState state)
        {
            Live[state.Target] = state;
            return TransformPortResult.Ok(state);
        }
        public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired,
            bool rawBaseline = false) => throw new NotSupportedException();
        public void Dispose() => Gestures.Dispose();
    }
}
