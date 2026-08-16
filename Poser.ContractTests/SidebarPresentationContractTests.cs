using System.Numerics;
using Poser.UI;

namespace Poser.ContractTests;

/// <summary>Contracts for scene-tree text and state marks.</summary>
public sealed class SidebarPresentationContractTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Section_plus_moves_only_its_ink(float scale)
    {
        var min = new Vector2(100f, 40f);
        var max = new Vector2(120f, 60f);

        var bounds = Crystarium.SidebarPlusInkBounds(min, max, scale);

        Assert.Equal(min.X - scale, bounds.Min.X);
        Assert.Equal(max.X - scale, bounds.Max.X);
        Assert.Equal(min.Y, bounds.Min.Y);
        Assert.Equal(max.Y, bounds.Max.Y);
    }

    [Fact]
    public void Mixed_visibility_splits_one_mark_without_selected_state()
    {
        var plan = Crystarium.SidebarVisibilitySplit(
            new Vector2(10f, 20f), new Vector2(30f, 40f));

        Assert.Equal(20f, plan.SplitX);
        Assert.Equal(0.45f, plan.InactiveOpacity);
        Assert.Equal(1f, plan.ActiveOpacity);
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
