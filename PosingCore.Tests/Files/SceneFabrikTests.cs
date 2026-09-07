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
            FabrikMode = FabrikControlMode.Bidirectional,
            Fabrik = new([
                new("root", 0, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity),
                new("tip", 0, Vector3.UnitX, Quaternion.Identity, Vector3.Zero, Quaternion.Identity)],
                Target(new(1, 2, 3)), Target(new(3, 2, 3)), 15),
        };
    }

    [Fact]
    public void Scene_payload_roundtrips_both_targets_and_chain_without_native_generation_ids()
    {
        var config = Config();
        var actor = new ActorId(Guid.NewGuid(), 73);
        var anchor = new BoneId(new(actor, PoseSlot.Character, 91), 1, 37, "anchor");
        config = config with { Fabrik = config.Fabrik! with
            { Root = config.Fabrik.Root with { Mode = IkTargetMode.Bone, Bone = anchor },
              Tip = config.Fabrik.Tip with { Mode = IkTargetMode.Entity,
                  Entity = SelectionId.ForLight(new(Guid.NewGuid(), 42)) } } };
        var saved = SceneFabrikChain.Capture(PoseSlot.Character, 0, "tip", config);
        var json = JsonSerializer.Serialize(saved, SceneJsonOptionsAccessor.Options);
        var read = JsonSerializer.Deserialize<SceneFabrikChain>(json, SceneJsonOptionsAccessor.Options)!;
        Assert.Null(read.Config.Validate());
        Assert.Equal(FabrikControlMode.Bidirectional, read.Config.FabrikMode);
        Assert.Equal(config.Fabrik.Bones, read.Config.Fabrik!.Bones);
        Assert.Equal(config.Fabrik.Root.Position, read.Config.Fabrik.Root.Position);
        Assert.Equal(actor.LogicalId, read.RootBone!.ActorKey);
        Assert.Equal("anchor", read.RootBone.BoneName);
        Assert.Null(read.Config.Fabrik.Root.Bone);
        Assert.Null(read.Config.Fabrik.Tip.Entity);
        Assert.DoesNotContain("Generation", json);
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
