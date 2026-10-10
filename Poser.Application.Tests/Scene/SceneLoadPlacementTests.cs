using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Scene;
using Poser.Files;

namespace Poser.Application.Tests.Scene;

public sealed class SceneLoadPlacementTests
{
    private static SceneFile TwoProps() => new()
    {
        SceneId = Guid.NewGuid(),
        Props =
        [
            new SceneProp { Key = Guid.NewGuid(), Name = "A",
                Transform = new LightFile.TransformData { Position = new(10, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } },
            new SceneProp { Key = Guid.NewGuid(), Name = "B",
                Transform = new LightFile.TransformData { Position = new(20, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } },
        ],
    };

    [Fact]
    public void An_entry_without_a_saved_anchor_lands_its_centre_on_the_current_anchor()
    {
        var scene = TwoProps();
        var notes = new List<string>();
        var options = new SceneLoadOptions
        {
            Placement = ObjectPlacementMode.RelativeToSelectedActor,
            PlacementPosition = new Vector3(0, 0, 5),
        };

        Assert.Null(SceneLoadPlacement.Apply(scene, options, null, notes));

        Assert.Equal(new Vector3(-5, 0, 5), scene.Props[0].Transform.Position);
        Assert.Equal(new Vector3(5, 0, 5), scene.Props[1].Transform.Position);
        Assert.Equal(
            new[]
            {
                "No saved anchor: the content's centre lands on the anchor instead.",
                "Placed relative to the actor.",
            },
            notes);
    }

    [Fact]
    public void A_relative_load_with_nobody_to_place_against_refuses_and_moves_nothing()
    {
        var scene = TwoProps();
        scene.Origin = Vector3.Zero;
        var notes = new List<string>();

        var refusal = SceneLoadPlacement.Apply(
            scene, new SceneLoadOptions { PlaceRelativeToCurrentOrigin = true }, null, notes);

        Assert.NotNull(refusal);
        Assert.Equal(new Vector3(10, 0, 0), scene.Props[0].Transform.Position);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_entry_that_places_nothing_loads_as_saved_and_says_so()
    {
        var scene = new SceneFile { SceneId = Guid.NewGuid() };
        var notes = new List<string>();

        Assert.Null(SceneLoadPlacement.Apply(
            scene, new SceneLoadOptions { Placement = ObjectPlacementMode.InFrontOfCamera }, null, notes));

        Assert.Null(SceneLoadPlacement.ContentCentroid(scene));
        Assert.Equal("The entry places nothing, so it loaded as saved.", Assert.Single(notes));
    }
}
