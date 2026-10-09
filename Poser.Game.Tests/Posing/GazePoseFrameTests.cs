using System.Numerics;
using Poser.Game.Posing;
using Poser.Files;
using Newtonsoft.Json;

namespace Poser.Game.Tests.Posing;

public sealed class GazePoseFrameTests
{
    [Fact]
    public void Inverse_target_mapping_preserves_target_under_rotation_translation_and_nonuniform_scale()
    {
        var raw = Matrix4x4.CreateFromYawPitchRoll(.4f, -.1f, .2f) * Matrix4x4.CreateTranslation(0, 1.5f, 0);
        var authored = Matrix4x4.CreateScale(1.4f, .8f, 1.1f)
            * Matrix4x4.CreateRotationZ(.5f) * Matrix4x4.CreateTranslation(.3f, .1f, -.2f);
        var world = Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(1)
            * Matrix4x4.CreateTranslation(20, 2, -50);
        var posed = raw * authored;
        var map = GazePoseFrames.Map(raw, posed, world)!.Value;
        var target = new Vector3(21, 4, -47);
        var input = Vector3.Transform(target, map);
        Assert.True(Matrix4x4.Invert(raw * world, out var inverse));
        var final = Vector3.Transform(Vector3.Transform(input, inverse), posed * world);
        Assert.True(Vector3.Distance(target, final) < .0001f);
    }

    [Fact]
    public void Unedited_native_gaze_cancels_instead_of_becoming_an_authored_delta()
    {
        var authored = Matrix4x4.CreateRotationZ(.35f) * Matrix4x4.CreateTranslation(.2f, 0, 0);
        var world = Matrix4x4.CreateTranslation(20, 0, 50);
        Matrix4x4? first = null;
        for (int i = 0; i < 20; i++)
        {
            var gaze = Matrix4x4.CreateRotationY(i * .05f) * Matrix4x4.CreateTranslation(0, 1, 0);
            var map = GazePoseFrames.Map(gaze, gaze * authored, world)!.Value;
            first ??= map;
            Assert.True(Vector3.Distance(Vector3.Transform(new(24, 2, 52), first.Value),
                Vector3.Transform(new(24, 2, 52), map)) < .0001f);
            var identity = GazePoseFrames.Map(gaze, gaze, world)!.Value;
            Assert.True(Vector3.Distance(new(24, 2, 52), Vector3.Transform(new(24, 2, 52), identity)) < .0001f);
        }
    }

    [Fact]
    public void Missing_or_singular_frames_are_not_used()
    {
        Assert.Null(GazePoseFrames.Map(null, Matrix4x4.Identity, Matrix4x4.Identity));
        Assert.Null(GazePoseFrames.Map(Matrix4x4.Identity, Matrix4x4.CreateScale(0), Matrix4x4.Identity));
        Assert.Null(GazePoseFrames.Map(Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.CreateScale(0)));
    }

    [Fact]
    public void Scene_roundtrip_preserves_opt_in_and_old_scenes_default_off()
    {
        Assert.False(JsonConvert.DeserializeObject<SceneActorGaze>("{}")!.PoseAware);
        var saved = JsonConvert.SerializeObject(new SceneActorGaze { PoseAware = true });
        Assert.True(JsonConvert.DeserializeObject<SceneActorGaze>(saved)!.PoseAware);
    }
}

