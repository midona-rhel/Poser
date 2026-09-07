using System;
using System.Numerics;
using System.Text.Json;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class SceneFabrikTests
{
    private static IkChainConfig Config()
    {
        FabrikTarget Target(Vector3 point) => new(IkTargetMode.World, point, Quaternion.Identity,
            Vector3.Zero, Quaternion.Identity);
        return IkChainConfig.DefaultsForChain(true) with
        {
            ParentDepth = 0, ChildDepth = 1,
            Fabrik = new([
                new("root", 0, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity),
                new("tip", 0, Vector3.UnitX, Quaternion.Identity, Vector3.Zero, Quaternion.Identity)],
                Target(new(1, 2, 3)), Target(new(3, 2, 3)), 15, 0, Target(new(1, 2, 3))),
        };
    }

    [Theory]
    [InlineData(IkSolver.Fabrik)]
    [InlineData(IkSolver.Rope)]
    public void Scene_payload_roundtrips_handle_anchors_and_span_without_native_generation_ids(IkSolver solver)
    {
        var config = Config() with { Solver = solver };
        var actor = new ActorId(Guid.NewGuid(), 73);
        var anchor = new BoneId(new(actor, PoseSlot.Character, 91), 1, 37, "anchor");
        config = config with { Fabrik = config.Fabrik! with
            { Handle = config.Fabrik.Handle with { Mode = IkTargetMode.Bone, Bone = anchor } } };
        var saved = SceneFabrikChain.Capture(PoseSlot.Character, 0, "tip", config);
        var json = JsonSerializer.Serialize(saved, SceneJsonOptionsAccessor.Options);
        var read = JsonSerializer.Deserialize<SceneFabrikChain>(json, SceneJsonOptionsAccessor.Options)!;
        Assert.Null(read.Config.Validate());
        Assert.Equal(solver, read.Config.Solver);
        Assert.Equal(0, read.Config.ParentDepth);
        Assert.Equal(1, read.Config.ChildDepth);
        Assert.Equal(config.Fabrik.Bones, read.Config.Fabrik!.Bones);
        Assert.Equal(config.Fabrik.Root.Position, read.Config.Fabrik.Root.Position);
        Assert.Equal(actor.LogicalId, read.HandleBone!.ActorKey);
        Assert.Equal("anchor", read.HandleBone.BoneName);
        Assert.Null(read.Config.Fabrik.Handle.Bone);
        Assert.DoesNotContain("Generation", json);
    }

    [Fact]
    public void Scene_entity_handle_uses_a_portable_reference()
    {
        var config = Config();
        var light = new LightId(Guid.NewGuid(), 42);
        config = config with { Fabrik = config.Fabrik! with { Handle = config.Fabrik.Handle with
            { Mode = IkTargetMode.Entity, Entity = SelectionId.ForLight(light) } } };
        var saved = SceneFabrikChain.Capture(PoseSlot.Character, 0, "root", config);
        Assert.Null(saved.Config.Fabrik!.Handle.Entity);
        Assert.Equal("light", saved.HandleEntity!.Kind);
        Assert.Equal(light.LogicalId, saved.HandleEntity.Key);
    }

    [Fact]
    public void Zero_depths_keep_the_handle_state_without_an_active_span()
    {
        var config = Config();
        config = config with { ParentDepth = 0, ChildDepth = 0, Fabrik = config.Fabrik! with
            { Bones = [config.Fabrik.Bones[0]], HandleIndex = 0 } };
        var json = JsonSerializer.Serialize(SceneFabrikChain.Capture(PoseSlot.Character, 0, "root", config),
            SceneJsonOptionsAccessor.Options);
        var read = JsonSerializer.Deserialize<SceneFabrikChain>(json, SceneJsonOptionsAccessor.Options)!;
        Assert.Null(read.Config.Validate());
        Assert.Single(read.Config.Fabrik!.Bones);
        Assert.Equal(config.Fabrik.Handle, read.Config.Fabrik.Handle);
    }

    [Fact]
    public void Placement_moves_world_points_rotates_offsets_and_leaves_actor_points_alone()
    {
        var config = Config();
        config = config with { Fabrik = config.Fabrik! with
            { Tip = config.Fabrik.Tip with { Mode = IkTargetMode.Entity } } };
        var scene = new SceneFile { Actors = [new SceneActor
            { Fabrik = [SceneFabrikChain.Capture(PoseSlot.Character, 0, "tip", config)] }] };
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f);
        Vector3 Move(Vector3 point) => Vector3.Transform(point, turn) + new Vector3(10, 20, 30);
        SceneFabrikChain.Rebase(scene, Move, turn);
        var restored = scene.Actors[0].Fabrik![0].Config.Fabrik!;
        Assert.Equal(Move(config.Fabrik.Root.Position), restored.Root.Position);
        Assert.Equal(Vector3.Transform(config.Fabrik.Tip.Position, turn), restored.Tip.Position);
        Assert.Equal(config.Fabrik.Bones, restored.Bones);
    }

    [Fact]
    public void Old_scene_actor_has_no_fabrik_payload()
    {
        var json = JsonSerializer.Serialize(new SceneActor(), SceneJsonOptionsAccessor.Options);
        Assert.DoesNotContain("Fabrik", json);
        Assert.Null(JsonSerializer.Deserialize<SceneActor>(json, SceneJsonOptionsAccessor.Options)!.Fabrik);
    }
}
