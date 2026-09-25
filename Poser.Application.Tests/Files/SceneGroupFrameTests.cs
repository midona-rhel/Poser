using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Poser.Files;
using Poser.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Tests.Files;

public sealed class SceneGroupFrameTests
{
    [Fact]
    public void Origin_placement_moves_frame_controls_and_both_snapshots()
    {
        var scene = Scene();
        scene.Origin = new(3, 4, 5);
        var saved = scene.Groups![0].Transform!;
        var before = saved.Members[0].Initial.Position;
        Assert.Null(SceneRelativePlacement.Rebase(scene, new(13, 24, 35)));
        Assert.Equal(new Vector3(10, 20, 30), saved.FrameOrigin);
        Assert.Equal(new Vector3(11, 20, 30), saved.Position);
        Assert.Equal(before + new Vector3(10, 20, 30), saved.Members[0].Initial.Position);
    }

    [Fact]
    public void Yaw_rebase_preserves_noncommuting_frame_relative_rotation()
    {
        var scene = Scene();
        var saved = scene.Groups![0].Transform!;
        var frame = new GroupTransformFrame(saved.FrameOrigin, saved.FrameRotation);
        var local = saved.Rotation;
        var initial = saved.Members[0].Initial;
        var expected = saved.Members[0].Expected;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .9f);
        Assert.Null(ScenePlacementRebase.Rebase(scene, new() { Position = Vector3.Zero, Yaw = 0 },
            Vector3.Zero, .9f));
        var movedFrame = new GroupTransformFrame(saved.FrameOrigin, saved.FrameRotation);
        Assert.Equal(Vector3.Zero, saved.FrameOrigin);
        Assert.Equal(local, saved.Rotation);
        var oldWorld = frame.ToWorldDelta(local);
        var newWorld = movedFrame.ToWorldDelta(local);
        Assert.True(MathF.Abs(Quaternion.Dot(newWorld,
            Quaternion.Normalize(turn * oldWorld * Quaternion.Inverse(turn)))) > .99999f);
        Assert.True(MathF.Abs(Quaternion.Dot(oldWorld, newWorld)) < .999f);
        Assert.True(GroupTransformReadModel.Equivalent(saved.Members[0].Initial,
            initial with { Position = Vector3.Transform(initial.Position, turn),
                Rotation = Quaternion.Normalize(turn * initial.Rotation) }));
        Assert.True(GroupTransformReadModel.Equivalent(saved.Members[0].Expected,
            expected with { Position = Vector3.Transform(expected.Position, turn),
                Rotation = Quaternion.Normalize(turn * expected.Rotation) }));
    }

    [Fact]
    public void Real_serializer_roundtrip_validates_and_decodes_complete_state()
    {
        var scene = Scene();
        Assert.Null(SceneGroupTransformCodec.Validate(scene));
        var json = JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);
        var read = JsonSerializer.Deserialize<SceneFile>(json, SceneJsonOptionsAccessor.Options)!;
        Assert.Null(SceneGroupTransformCodec.Validate(read));
        var a = TransformTargetId.ForActor(ActorId.New());
        var b = TransformTargetId.ForProp(PropId.New());
        var decoded = SceneGroupTransformCodec.Decode(read.Groups![0].Transform!, [a, b],
            reference => reference.Kind == "actor" ? a : b);
        Assert.NotNull(decoded);
        Assert.Equal(new Vector3(10000), decoded.Controls.SpacingScale);
        Assert.Equal(new Vector3(100), decoded.Controls.OwnScale);
    }

    [Fact]
    public void Legacy_absent_metadata_is_supported_but_invalid_present_state_is_not()
    {
        var scene = Scene();
        scene.Groups![0].Transform = null;
        Assert.Null(SceneGroupTransformCodec.Validate(scene));
        scene.Groups[0].Transform = new();
        Assert.NotNull(SceneGroupTransformCodec.Validate(scene));
    }

    [Fact]
    public void Membership_changed_owned_controls_and_member_poses_survive_scene_roundtrip()
    {
        var actor = TransformTargetId.ForActor(ActorId.New());
        var prop = TransformTargetId.ForProp(PropId.New());
        var removed = TransformTargetId.ForActor(ActorId.New());
        var originalMembers = new Dictionary<TransformTargetId, PoseTransform> {
            [actor] = new(new(2, 4, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f), new(1, 2, 1)),
            [prop] = new(new(8, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.4f), new(2, 1, 3)),
            [removed] = Pose(new(20, 0, 0))
        };
        Assert.True(GroupTransformBaseline.TryCapture(originalMembers,
            new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .7f)), out var baseline, out _));
        var controls = new GroupTransformControls(GroupTransformBaseline.Centroid(originalMembers.Values),
            Quaternion.CreateFromYawPitchRoll(.6f, -.3f, .2f), new(3, 4, 2), new(1.5f, 2, 1));
        var original = new GroupTransformSnapshot(baseline!, originalMembers, controls);
        var members = originalMembers.Where(pair => pair.Key != removed).ToDictionary();
        var changed = original.WithMembership(members)!;
        var scene = SceneFileStoreTests.ValidScene();
        var actorRef = new SceneStructureRef { Kind = "actor", Key = scene.Actors[0].Key };
        var propRef = new SceneStructureRef { Kind = "prop", Key = scene.Props[0].Key };
        var saved = new SceneGroupTransformEntry {
            FrameOrigin = changed.Baseline.Frame.Origin, FrameRotation = changed.Baseline.Frame.Rotation,
            Position = changed.Controls.Position, Rotation = changed.Controls.Rotation,
            SpacingScale = changed.Controls.SpacingScale, OwnScale = changed.Controls.OwnScale,
            Members = members.Select(pair => new SceneGroupTransformMember {
                Member = pair.Key == actor ? actorRef : propRef,
                Initial = changed.Baseline.InitialTransforms[pair.Key], Expected = pair.Value
            }).ToList()
        };
        scene.Groups = [new() { Key = Guid.NewGuid(), Name = "Edited membership",
            Members = [actorRef, propRef], Transform = saved }];
        var json = JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);
        var restoredScene = JsonSerializer.Deserialize<SceneFile>(json, SceneJsonOptionsAccessor.Options)!;
        Assert.Null(SceneGroupTransformCodec.Validate(restoredScene));
        var restored = SceneGroupTransformCodec.Decode(restoredScene.Groups![0].Transform!,
            [actor, prop], reference => reference.Kind == "actor" ? actor : prop)!;
        Assert.Equal(controls with { Position = GroupTransformBaseline.Centroid(members.Values) }, restored.Controls);
        Assert.Equal(changed.Baseline.Frame.Origin, restored.Baseline.Frame.Origin);
        Assert.True(MathF.Abs(Quaternion.Dot(changed.Baseline.Frame.Rotation,
            restored.Baseline.Frame.Rotation)) > .99999f);
        Assert.Equal(members, restored.Expected);
        Assert.All(members, pair => Assert.True(GroupTransformReadModel.Equivalent(
            pair.Value, restored.Baseline.InitialTransforms[pair.Key])));
        Assert.True(GroupTransformReadModel.TryRead(restored, members, GroupScaleMode.SpacingOnly, out var read, out _));
        Assert.Equal(changed.Controls.Position, read.Position);
        Assert.Equal(controls.Rotation, read.Rotation);
        Assert.Equal(controls.SpacingScale, read.Scale);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-kind")]
    [InlineData("zero")]
    [InlineData("overflow")]
    public void Corrupt_present_records_are_refused(string corruption)
    {
        var scene = Scene();
        var saved = scene.Groups![0].Transform!;
        switch (corruption)
        {
            case "missing": saved.Members.RemoveAt(0); break;
            case "duplicate": saved.Members[1].Member = saved.Members[0].Member; break;
            case "wrong-kind": saved.Members[0].Member = new() { Kind = "camera", Key = Guid.NewGuid() }; break;
            case "zero": saved.OwnScale = Vector3.Zero; break;
            case "overflow": saved.SpacingScale = new(float.PositiveInfinity); break;
        }
        Assert.NotNull(SceneGroupTransformCodec.Validate(scene));
    }

    [Fact]
    public void Child_only_parent_uses_effective_descendants_for_saved_membership()
    {
        var scene = Scene();
        var child = scene.Groups![0];
        var parent = new SceneGroupEntry { Key = Guid.NewGuid(), Name = "Parent", Transform = child.Transform };
        child.Parent = parent.Key;
        scene.Groups.Add(parent);
        Assert.Null(SceneGroupTransformCodec.Validate(scene));
        parent.Parent = child.Key;
        Assert.NotNull(SceneGroupTransformCodec.Validate(scene));
    }

    private static SceneFile Scene()
    {
        var scene = SceneFileStoreTests.ValidScene();
        var a = new SceneStructureRef { Kind = "actor", Key = scene.Actors[0].Key };
        var b = new SceneStructureRef { Kind = "prop", Key = scene.Props[0].Key };
        scene.Groups = [new SceneGroupEntry {
            Key = Guid.NewGuid(), Name = "Pair", Members = [a, b],
            Transform = new() {
                FrameOrigin = Vector3.Zero,
                FrameRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f),
                Position = Vector3.UnitX,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f),
                SpacingScale = new(10000), OwnScale = new(100),
                Members = [new() { Member = a, Initial = Pose(Vector3.Zero), Expected = Pose(Vector3.UnitX) },
                    new() { Member = b, Initial = Pose(new(2, 0, 0)), Expected = Pose(new(3, 0, 0)) }]
            }
        }];
        return scene;
    }
    private static PoseTransform Pose(Vector3 position) => new(position, Quaternion.Identity, Vector3.One);
}
