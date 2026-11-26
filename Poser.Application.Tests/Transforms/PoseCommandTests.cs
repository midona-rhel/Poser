using System.Numerics;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class PoseCommandTests
{
    [Fact]
    public void Mirror_includes_facing()
    {
        using var f = new Fixture();
        var original = f.Live.ToDictionary();
        Assert.True(f.Commands.Mirror(f.Actor).Success);
        var mirror = Assert.IsType<TransformPatch>(f.History.PeekUndo());
        Assert.Contains(mirror.After, state => state.Target == f.Model);
        Assert.Equal(original[f.Model].Transform.Position, f.Live[f.Model].Transform.Position);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
    }

    [Fact]
    public void Old_actor_and_bone_commands_do_not_redirect_to_new_generation()
    {
        using var f = new Fixture();
        var replacement = f.Actor with { Generation = f.Actor.Generation + 1 };
        Assert.True(f.Scene.TryRefresh(new SceneSnapshot(2,
            [new ActorDescriptor(replacement, "replacement", [])], [], [], [])).Accepted);
        f.Captures = 0;
        Assert.False(f.Commands.Mirror(f.Actor).Success);
        Assert.False(f.Commands.ResetBone(f.Character, "old bone").Success);
        Assert.False(f.Commands.HasAuthoredEdits(f.Actor));
        Assert.Equal(0, f.Captures);
        Assert.False(f.History.CanUndo);
    }

    private sealed class Fixture : ITransformRuntimePort, IPoseEditReads, IDisposable
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly SceneSession Scene = new(new SelectionSession());
        public readonly EditHistory History = new();
        public readonly Dictionary<TransformTargetId, TransformTargetState> Live = new();
        public readonly TransformTargetId Character, Weapon, Model;
        public readonly TransformGestureService Gestures;
        public readonly UndoJournal Journal;
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
            Journal = new(History, Gestures, _ => true, new Fixtures.NoticeLog());
            var transforms = new TransformCommands(this, History, Gestures);
            var edits = new PoseEditService(this, transforms);
            Commands = new PoseCommands(Scene, edits, new(null!, null!, null!, Scene, null!), this);
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
