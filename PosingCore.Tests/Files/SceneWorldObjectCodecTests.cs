using System;
using System.IO;
using System.Numerics;
using Poser.Domain.Presentation;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class SceneWorldObjectCodecTests
{
    [Fact]
    public void Scene_codecs_round_trip_world_objects_and_overlay_payloads()
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
        ];
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new OverlayNodeState
                {
                    Kind = OverlayNodeKind.Talk,
                    Name = "Opening line",
                    Position = new Vector2(320f, 640f),
                    Speaker = "Y'shtola",
                    Text = "The aether stirs.",
                    TalkCursor = TalkCursor.Loop,
                },
            },
        ];

        Assert.True(SceneFileStore.Default.Write(scene, file.Path).Succeeded);
        var read = SceneFileStore.Default.Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        var world = Assert.Single(read.Scene!.WorldObjects!);
        Assert.Equal(key, world.Key);
        Assert.Equal(new Vector3(12.5f, -3.25f, 88f), world.MapPosition);
        Assert.False(world.Visible);
        var overlay = Assert.Single(read.Scene.Overlays!);
        Assert.Equal("Y'shtola", overlay.Node!.Speaker);
        Assert.Equal(TalkCursor.Loop, overlay.Node.TalkCursor);
        Assert.Equal(new Vector2(320f, 640f), overlay.Node.Position);
    }

    [Fact]
    public void Optional_codec_collections_are_absent_when_empty_and_unknown_members_are_ignored()
    {
        using var file = new TempWorldScene();
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects = null;
        scene.Overlays = null;
        var json = System.Text.Json.JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);
        json = json.TrimEnd()[..^1] + ",\"FutureMember\":true}";
        File.WriteAllText(file.Path, json);

        var read = SceneFileStore.Default.Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.WorldObjects);
        Assert.Null(read.Scene.Overlays);
        Assert.DoesNotContain("WorldObjects", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Overlays", json, StringComparison.Ordinal);
    }

    [Fact]
    public void World_and_overlay_validation_preserves_identity_numeric_and_size_guards()
    {
        var scene = SceneFileStoreTests.ValidScene();
        var key = Guid.NewGuid();
        scene.WorldObjects =
        [
            new SceneWorldObject { Key = key, Path = "bg/a.mdl", MapPosition = new Vector3(float.NaN, 0, 0) },
            new SceneWorldObject { Key = key, Path = "bg/b.mdl" },
        ];
        scene.Overlays = [new SceneOverlay { Key = Guid.NewGuid() }];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failure!.Kind,
            new[] { SceneFileValidationFailureKind.Identity,
                SceneFileValidationFailureKind.NonFiniteNumeric,
                SceneFileValidationFailureKind.Document });
    }

    private sealed class TempWorldScene : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-worldobject-{Guid.NewGuid():N}.poserscene");

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
