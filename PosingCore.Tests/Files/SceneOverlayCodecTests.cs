using System;
using System.IO;
using System.Numerics;
using Poser.Domain.Presentation;
using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// The overlay list's codec contract. Two things have to hold together: a
/// staged node comes back whole, AND a scene without one writes exactly the
/// file it wrote before overlay nodes existed — so a library full of older
/// scenes is untouched by this feature and an older Poser reading a newer file
/// simply ignores a member it does not know.
/// </summary>
public sealed class SceneOverlayCodecTests
{
    [Fact]
    public void A_staged_node_round_trips_whole()
    {
        using var file = new TempScene();
        var scene = SceneFileStoreTests.ValidScene();
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
                    Scale = 1.25f,
                    Alpha = 0.8f,
                    Speaker = "Y'shtola",
                    Text = "The aether stirs.",
                    FontSize = 16,
                    TalkBackground = TalkBackground.Linkpearl,
                    TalkCursor = TalkCursor.Loop,
                },
            },
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new OverlayNodeState
                {
                    Kind = OverlayNodeKind.Balloon,
                    Name = "Aside",
                    Text = "Hmm.",
                    BalloonChannel = BalloonChannel.FreeCompany,
                    BalloonGradient = BalloonGradient.RoyalPurple,
                    ArrowVisible = false,
                    ArrowX = 64f,
                },
            },
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new OverlayNodeState
                {
                    Kind = OverlayNodeKind.Status,
                    Name = "Astral Fire",
                    Text = "Astral Fire III",
                    StatusKind = StatusKind.Falloff,
                    StatusIconId = 212563,
                    Visible = false,
                },
            },
        ];

        Assert.True(new SceneFileStore().Write(scene, file.Path).Succeeded);
        var read = new SceneFileStore().Read(file.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);

        var overlays = read.Scene!.Overlays!;
        Assert.Equal(3, overlays.Count);
        Assert.Equal(scene.Overlays[0].Key, overlays[0].Key);
        Assert.Equal(scene.Overlays[0].Node, overlays[0].Node);
        Assert.Equal(scene.Overlays[1].Node, overlays[1].Node);
        Assert.Equal(scene.Overlays[2].Node, overlays[2].Node);
        // Spot-checked by hand as well as by record equality, so a field
        // silently dropped by a converter cannot pass on reference identity.
        Assert.Equal("Y'shtola", overlays[0].Node!.Speaker);
        Assert.Equal(TalkCursor.Loop, overlays[0].Node!.TalkCursor);
        Assert.Equal(new Vector2(320f, 640f), overlays[0].Node!.Position);
        Assert.False(overlays[1].Node!.ArrowVisible);
        Assert.Equal(212563u, overlays[2].Node!.StatusIconId);
        Assert.False(overlays[2].Node!.Visible);
    }

    [Fact]
    public void A_scene_with_no_staged_node_writes_no_overlay_member()
    {
        using var file = new TempScene();
        var scene = SceneFileStoreTests.ValidScene();

        Assert.True(new SceneFileStore().Write(scene, file.Path).Succeeded);

        Assert.DoesNotContain("Overlays", File.ReadAllText(file.Path));
    }

    [Fact]
    public void A_scene_written_before_overlays_existed_reads_back_unchanged()
    {
        using var file = new TempScene();
        var scene = SceneFileStoreTests.ValidScene();
        Assert.True(new SceneFileStore().Write(scene, file.Path).Succeeded);

        var read = new SceneFileStore().Read(file.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.Overlays);
    }

    [Fact]
    public void An_overlay_entry_without_a_node_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.Overlays = [new SceneOverlay { Key = Guid.NewGuid() }];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Document, result.Failure!.Kind);
    }

    [Fact]
    public void An_overlay_entry_without_a_key_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.Overlays =
        [
            new SceneOverlay { Node = new OverlayNodeState { Name = "A" } },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Identity, result.Failure!.Kind);
    }

    [Fact]
    public void Two_overlays_sharing_one_key_are_refused()
    {
        var key = Guid.NewGuid();
        var scene = SceneFileStoreTests.ValidScene();
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = key,
                Node = new OverlayNodeState { Name = "A" },
            },
            new SceneOverlay
            {
                Key = key,
                Node = new OverlayNodeState { Name = "B" },
            },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Identity, result.Failure!.Kind);
    }

    [Fact]
    public void An_overlay_carrying_a_non_finite_value_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new OverlayNodeState
                {
                    Name = "A",
                    Scale = float.NaN,
                },
            },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Range, result.Failure!.Kind);
    }

    [Fact]
    public void An_overlay_beyond_the_text_cap_is_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new OverlayNodeState
                {
                    Name = "A",
                    Text = new string(
                        'x', OverlayNodeLimits.MaxTextCharacters + 1),
                },
            },
        ];

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.Range, result.Failure!.Kind);
    }

    [Fact]
    public void More_overlays_than_the_cap_are_refused()
    {
        var scene = SceneFileStoreTests.ValidScene();
        scene.Overlays = [];
        for (int i = 0; i <= SceneFileLimits.MaxOverlays; i++)
            scene.Overlays.Add(new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new OverlayNodeState { Name = "Node" },
            });

        var result = SceneFileValidation.Validate(scene);

        Assert.False(result.Succeeded);
        Assert.Equal(
            SceneFileValidationFailureKind.CollectionSize, result.Failure!.Kind);
    }

    private sealed class TempScene : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"poser-overlay-{Guid.NewGuid():N}.poserscene");

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
