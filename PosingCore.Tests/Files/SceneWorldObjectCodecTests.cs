using System;
using System.IO;
using System.Numerics;
using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// The borrowed-object list's codec contract. Two things have to hold together:
/// a borrowed object comes back with BOTH halves of its identity and the
/// placement the user gave it, AND a scene that borrowed nothing writes exactly
/// the file it wrote before world objects could be adopted — so a library full
/// of older scenes is untouched by this feature.
/// </summary>
public sealed class SceneWorldObjectCodecTests
{
    [Fact]
    public void A_borrowed_object_round_trips_whole()
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
                    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f),
                    Scale = new Vector3(2f, 2f, 2f),
                },
                Visible = false,
            },
        ];
        Assert.True(new SceneFileStore().Write(scene, file.Path).Succeeded);

        var read = new SceneFileStore().Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        var borrowed = Assert.Single(read.Scene!.WorldObjects!);
        Assert.Equal(key, borrowed.Key);
        Assert.Equal(
            "bg/ffxiv/fst_f1/twn/f1t2/bgparts/f1t2_a1_bals1.mdl", borrowed.Path);
        Assert.Equal(new Vector3(12.5f, -3.25f, 88f), borrowed.MapPosition);
        Assert.Equal(new Vector3(14f, -3f, 90f), borrowed.Transform.Position);
        Assert.Equal(new Vector3(2f, 2f, 2f), borrowed.Transform.Scale);
        Assert.False(borrowed.Visible);
    }

    [Fact]
    public void A_scene_that_borrowed_nothing_writes_no_list_at_all()
    {
        using var file = new TempWorldScene();
        var scene = SceneFileStoreTests.ValidScene();
        Assert.True(new SceneFileStore().Write(scene, file.Path).Succeeded);

        string json = File.ReadAllText(file.Path);
        var read = new SceneFileStore().Read(file.Path);

        Assert.DoesNotContain("WorldObjects", json, StringComparison.Ordinal);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.WorldObjects);
    }

    [Fact]
    public void A_world_object_without_a_path_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects =
        [
            new SceneWorldObject { Key = Guid.NewGuid(), Path = string.Empty },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_world_object_without_a_key_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects = [new SceneWorldObject { Path = "bg/a.mdl" }];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Identity, result.Failure!.Kind);
    }

    [Fact]
    public void Two_world_objects_sharing_one_key_are_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        var key = Guid.NewGuid();
        scene.WorldObjects =
        [
            new SceneWorldObject { Key = key, Path = "bg/a.mdl" },
            new SceneWorldObject { Key = key, Path = "bg/b.mdl" },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Identity, result.Failure!.Kind);
    }

    /// <summary>The map position is HALF THE IDENTITY, so a non-finite one is
    /// an entry that could match anything or nothing.</summary>
    [Fact]
    public void A_world_object_with_a_non_finite_map_position_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects =
        [
            new SceneWorldObject
            {
                Key = Guid.NewGuid(),
                Path = "bg/a.mdl",
                MapPosition = new Vector3(float.NaN, 0f, 0f),
            },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.NonFiniteNumeric, result.Failure!.Kind);
    }

    [Fact]
    public void More_world_objects_than_the_limit_are_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects = [];
        for (int i = 0; i <= SceneFileLimits.MaxWorldObjects; i++)
        {
            scene.WorldObjects.Add(new SceneWorldObject
            {
                Key = Guid.NewGuid(),
                Path = $"bg/{i}.mdl",
            });
        }

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.CollectionSize, result.Failure!.Kind);
    }

    private sealed class TempWorldScene : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"poser-worldobject-{Guid.NewGuid():N}.poserscene");

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
