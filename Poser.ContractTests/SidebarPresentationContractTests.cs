using System.Numerics;
using Poser.UI;

namespace Poser.ContractTests;

/// <summary>Contracts for scene-tree text and state marks.</summary>
public sealed class SidebarPresentationContractTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Section_plus_matches_the_trailing_action_geometry(float scale)
    {
        var contentRightTop = new Vector2(200f * scale, 40f * scale);

        SidebarTrailingActionGeometry geometry =
            Crystarium.SidebarTrailingAction(
                contentRightTop,
                bandHeight: 20f,
                actionSide: 20f,
                contentScale: 0.7f,
                trailingGap: 2f,
                scale);

        Assert.Equal(new Vector2(178f, 40f) * scale, geometry.HitMin);
        Assert.Equal(new Vector2(198f, 60f) * scale, geometry.HitMax);
        Assert.Equal(new Vector2(188f, 50f) * scale, geometry.Center);
        Assert.Equal(new Vector2(181f, 43f) * scale, geometry.GlyphMin);
        Assert.Equal(new Vector2(195f, 57f) * scale, geometry.GlyphMax);
        Assert.Equal(14f * scale, geometry.GlyphSide);
        Assert.Equal(geometry.Center,
            (geometry.HitMin + geometry.HitMax) * 0.5f);
        Assert.Equal(geometry.Center,
            (geometry.GlyphMin + geometry.GlyphMax) * 0.5f);
        Assert.Equal(new Vector2(178f, 60f) * scale,
            geometry.SpawnAnchor);
    }

    [Fact]
    public void Mixed_visibility_fills_only_the_inactive_eyes_pupil()
    {
        var plan = Crystarium.SidebarChildVisibility(
            new Vector2(10f, 20f), new Vector2(30f, 40f));

        Assert.Equal(0.45f, plan.EyeOpacity);
        Assert.Equal(new Vector2(20f, 30f), plan.PupilCenter);
        Assert.Equal(1.5f, plan.PupilRadius);
        Assert.Equal(1f, plan.PupilOpacity);
    }

    [Fact]
    public void Scene_tree_labels_use_full_chrome_ink()
    {
        Theme light = Theme.PictoLight;

        TextStyle style = Crystarium.SidebarTreeLabelStyle(light, null);

        Assert.Equal(light.Chrome.Text, style.Color);
        Assert.Equal(1f, style.Color!.Value.W);
        Assert.NotEqual(light.Text, style.Color);
        Assert.Equal(light.Typography.BodySize, style.Size);
    }
}
