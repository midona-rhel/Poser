using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Poser.Files;
using Poser.Documents.Files;

namespace Poser.Tests.Files;

public sealed class SceneWorldObjectCodecTests
{
    [Fact]
    public void Scene_codec_round_trips_world_objects()
    {
        using var file = new TempWorldScene();
        var scene = SceneFileStoreTests.ValidScene();
        var key = Guid.NewGuid();
        scene.WorldObjects =
        [
            new SceneWorldObject
            {
                Key = key,
                Path = "bg/ffxiv/fst_f1/twn/f1t2/bgparts/f1t2_a1_bals1.mdl",
                MapPosition = new Vector3(12.5f, -3.25f, 88f),
                Transform = new LightFile.TransformData
                {
                    Position = new Vector3(14f, -3f, 90f),
                    Rotation = Quaternion.Identity,
                    Scale = new Vector3(2f, 2f, 2f),
                },
                Visible = false,
            },
            new SceneWorldObject
            {
                Key = Guid.NewGuid(),
                Path = "bgcommon/hou/indoor/general/0001/asset/fun_b0_m0001.sgb",
                Name = "Chair 1", Spawned = true, Stain = 42,
                FurnitureLights = [new("/0", false), new("/2/0", true)],
                Tint = new Vector3(.2f, .3f, .4f), Opacity = .4f,
            },
        ];
        Assert.True(SceneFileStore.Default.Write(scene, file.Path).Succeeded);
        var read = SceneFileStore.Default.Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(2, read.Scene!.WorldObjects!.Count);
        var world = read.Scene.WorldObjects[0];
        Assert.Equal(key, world.Key);
        Assert.Equal(new Vector3(12.5f, -3.25f, 88f), world.MapPosition);
        Assert.False(world.Visible);
        var furniture = read.Scene.WorldObjects[1];
        Assert.Equal((byte)42, furniture.Stain);
        Assert.Equal(scene.WorldObjects[1].FurnitureLights, furniture.FurnitureLights);
        Assert.Equal(scene.WorldObjects[1].Tint, furniture.Tint);
        Assert.Equal(.4f, furniture.Opacity);
        Assert.True(furniture.Spawned);
    }

    private sealed class TempWorldScene : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-worldobject-{Guid.NewGuid():N}{SceneFile.Extension}");

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            var name = System.IO.Path.GetFileName(Path);
            foreach (var leftover in Directory.GetFiles(directory, $".{name}.*"))
                File.Delete(leftover);
        }
    }
}
