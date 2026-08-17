using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Poser.Files;

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
        ];
        Assert.True(SceneFileStore.Default.Write(scene, file.Path).Succeeded);
        var read = SceneFileStore.Default.Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        var world = Assert.Single(read.Scene!.WorldObjects!);
        Assert.Equal(key, world.Key);
        Assert.Equal(new Vector3(12.5f, -3.25f, 88f), world.MapPosition);
        Assert.False(world.Visible);
    }

    [Fact]
    public void Optional_world_object_collection_is_absent_and_unknown_members_are_ignored()
    {
        using var file = new TempWorldScene();
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects = null;
        var json = System.Text.Json.JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);
        json = json.TrimEnd()[..^1] + ",\"FutureMember\":true}";
        File.WriteAllText(file.Path, json);

        var read = SceneFileStore.Default.Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.WorldObjects);
        Assert.DoesNotContain("WorldObjects", json, StringComparison.Ordinal);
    }

    [Fact]
    public void World_object_validation_preserves_identity_and_numeric_guards()
    {
        var scene = SceneFileStoreTests.ValidScene();
        var key = Guid.NewGuid();
        scene.WorldObjects =
        [
            new SceneWorldObject { Key = key, Path = "bg/a.mdl", MapPosition = new Vector3(float.NaN, 0, 0) },
            new SceneWorldObject { Key = key, Path = "bg/b.mdl" },
        ];
        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failure!.Kind,
            new[] { SceneFileValidationFailureKind.Identity,
                SceneFileValidationFailureKind.NonFiniteNumeric,
                SceneFileValidationFailureKind.Document });
    }

    [Fact]
    public void Scene_codec_reports_each_world_object_guard()
    {
        var cases = new
        (Func<string> Json, SceneStoreFailureKind StoreKind,
            SceneFileValidationFailureKind? ValidationKind)[]
        {
            (() => SerializeScene(MissingWorldKey()), SceneStoreFailureKind.Validation,
                SceneFileValidationFailureKind.Identity),
            (() => SerializeScene(DuplicateWorldKey()), SceneStoreFailureKind.Validation,
                SceneFileValidationFailureKind.Identity),
            (NonFiniteWorldPositionJson, SceneStoreFailureKind.Json, null),
            (() => SerializeScene(OversizedWorldObjectList()), SceneStoreFailureKind.Validation,
                SceneFileValidationFailureKind.CollectionSize),
        };

        foreach (var testCase in cases)
        {
            var json = testCase.Json();
            var result = SceneFileStore.Default.Parse(json);

            Assert.False(result.Succeeded, testCase.StoreKind.ToString());
            Assert.Equal(testCase.StoreKind, result.Failure!.Kind);
            if (testCase.ValidationKind is { } validationKind)
                Assert.Equal(validationKind, result.Failure.ValidationFailure!.Kind);
            else
                Assert.Contains("invalid numeric value", result.Failure.Detail,
                    StringComparison.Ordinal);
        }
    }

    private static SceneFile MissingWorldKey()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects = [new SceneWorldObject { Path = "bg/a.mdl" }];
        return scene;
    }

    private static SceneFile DuplicateWorldKey()
    {
        var scene = SceneFileStoreTests.ValidScene();
        var key = Guid.NewGuid();
        scene.WorldObjects =
        [
            new SceneWorldObject { Key = key, Path = "bg/a.mdl" },
            new SceneWorldObject { Key = key, Path = "bg/b.mdl" },
        ];
        return scene;
    }

    private static string NonFiniteWorldPositionJson()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects =
        [new SceneWorldObject
        {
            Key = Guid.NewGuid(),
            Path = "bg/a.mdl",
        }];
        var json = SerializeScene(scene);
        return json.Replace(
            "\"MapPosition\": \"0, 0, 0\"",
            "\"MapPosition\": \"NaN, 0, 0\"",
            StringComparison.Ordinal);
    }

    private static string SerializeScene(SceneFile scene) =>
        System.Text.Json.JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);

    private static SceneFile OversizedWorldObjectList()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.WorldObjects = Enumerable.Range(0, SceneFileLimits.MaxWorldObjects + 1)
            .Select(index => new SceneWorldObject
            {
                Key = Guid.NewGuid(),
                Path = $"bg/{index}.mdl",
            })
            .ToList();
        return scene;
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
