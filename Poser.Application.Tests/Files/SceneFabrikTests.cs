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

    [Fact]
    public void Scene_payload_roundtrips_handle_anchors_and_span_without_native_generation_ids()
    {
        const IkSolver solver = IkSolver.Rope;
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
    public void Old_scene_actor_has_no_fabrik_payload()
    {
        var json = JsonSerializer.Serialize(new SceneActor(), SceneJsonOptionsAccessor.Options);
        Assert.DoesNotContain("Fabrik", json);
        Assert.Null(JsonSerializer.Deserialize<SceneActor>(json, SceneJsonOptionsAccessor.Options)!.Fabrik);
    }
}
