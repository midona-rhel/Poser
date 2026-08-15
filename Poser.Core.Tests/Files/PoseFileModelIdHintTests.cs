using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

/// <summary>
/// The Smart Import metadata hint on the wire: Brio's top-level
/// <c>ModelId</c>/<c>RaceSexId</c>/<c>FaceID</c> (Brio Files/PoseFile.cs:
/// 143-145). Export writes the actor's current model id; parse and re-save
/// keep a Brio-authored file's hint intact instead of silently dropping it.
/// </summary>
public sealed class PoseFileModelIdHintTests
{
    [Fact]
    public void Export_writes_the_actors_current_model_id()
    {
        var (skeleton, actor) = Skeleton();
        var spawn = Substitute.For<IActorSpawnService>();
        spawn.GetModelCharaId(actor).Returns(878);

        var pose = Service(spawn).CreatePoseFile(new[] { skeleton });

        Assert.Equal(878, pose.ModelId);
    }

    [Fact]
    public void Export_without_spawn_plumbing_carries_the_zero_default()
    {
        var (skeleton, _) = Skeleton();

        var pose = Service(spawn: null).CreatePoseFile(new[] { skeleton });

        Assert.Equal(0, pose.ModelId);
    }

    [Fact]
    public void Brio_authored_hint_survives_parse_and_resave()
    {
        const string brioJson = """
            {
              "Bones": {
                "j_kao": { "Position": "0, 0, 0", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1" }
              },
              "ModelId": 878,
              "RaceSexId": "0101",
              "FaceID": 1
            }
            """;

        var parsed = AtomicPoseFileStore.Default.Parse(brioJson).Pose;
        Assert.NotNull(parsed);
        Assert.Equal(878, parsed!.ModelId);
        Assert.Equal("0101", parsed.RaceSexId);
        Assert.Equal(1, parsed.FaceID);

        var json = System.Text.Json.JsonSerializer.Serialize(
            parsed, PoseFile.JsonOptions);
        var reparsed = AtomicPoseFileStore.Default.Parse(json).Pose;
        Assert.NotNull(reparsed);
        Assert.Equal(878, reparsed!.ModelId);
        Assert.Equal("0101", reparsed.RaceSexId);
        Assert.Equal(1, reparsed.FaceID);
    }

    private static PoseFileService Service(IActorSpawnService? spawn)
    {
        var posing = Substitute.For<IPosingService>();
        posing.GetEffectiveTransform(Arg.Any<IActor>()).Returns(Transform.Identity);
        posing.GetOriginalTransform(Arg.Any<IActor>()).Returns(Transform.Identity);
        return new PoseFileService(Substitute.For<IPluginLog>(), posing, spawn);
    }

    private static (ISkeleton Skeleton, IActor Actor) Skeleton()
    {
        var actor = Substitute.For<IActor>();
        var bone = Substitute.For<IBone>();
        bone.BoneName.Returns("j_kao");
        bone.LastRawTransform.Returns(new Transform(
            Vector3.Zero, Quaternion.Identity, Vector3.One));
        var skeleton = Substitute.For<ISkeleton>();
        skeleton.Slot.Returns(PoseSlot.Character);
        skeleton.Bones.Returns(new[] { bone });
        skeleton.Actor.Returns(actor);
        bone.Skeleton.Returns(skeleton);
        return (skeleton, actor);
    }
}
