using Poser.Application.Scene;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Scene;

public sealed class ScenePlacementPreferencesTests
{
    [Theory]
    [InlineData(10u, 10u, ObjectPlacementMode.AsSaved)]
    [InlineData(10u, 11u, ObjectPlacementMode.InFrontOfCamera)]
    [InlineData(0u, 0u, ObjectPlacementMode.InFrontOfCamera)]
    [InlineData(0u, 10u, ObjectPlacementMode.InFrontOfCamera)]
    public void Display_and_load_agree_for_known_and_unknown_maps(uint saved, uint current, ObjectPlacementMode expected)
    {
        var preferences = new SceneLoadPreferences();
        Assert.Equal(expected, preferences.PlacementFor("scene.xivs", saved, current, ObjectPlacementMode.InFrontOfCamera));
        var options = preferences.ForLoad("scene.xivs", current, ObjectPlacementMode.InFrontOfCamera);
        var resolved = SceneLoadPreferences.ResolveDefault(options, saved);
        Assert.Equal(expected, resolved.Placement);
        Assert.Equal(0u, resolved.DefaultPlacementTerritory);
        Assert.False(resolved.PlaceRelativeToCurrentOrigin);
    }

    [Fact]
    public void Explicit_placement_wins_only_for_the_selected_file()
    {
        var preferences = new SceneLoadPreferences();
        preferences.SetPlacement("scene.xivs", ObjectPlacementMode.RelativeToCamera);
        var chosen = preferences.ForLoad("scene.xivs", 10, ObjectPlacementMode.InFrontOfCamera);
        Assert.Equal(ObjectPlacementMode.RelativeToCamera, SceneLoadPreferences.ResolveDefault(chosen, 10).Placement);
        var other = preferences.ForLoad("other.xivs", 10, ObjectPlacementMode.InFrontOfCamera);
        Assert.Equal(ObjectPlacementMode.AsSaved, SceneLoadPreferences.ResolveDefault(other, 10).Placement);
    }
}
