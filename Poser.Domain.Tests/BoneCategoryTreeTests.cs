using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class BoneCategoryTreeTests
{
    [Fact]
    public void Bones_resolve_to_their_tree_category_and_unlisted_bones_to_none()
    {
        Assert.Equal("Head", KtisisBoneCategories.CategoryOf("j_f_ago")?.Id);
        Assert.Equal("Hair", KtisisBoneCategories.CategoryOf("j_kami_a")?.Id);
        Assert.Null(KtisisBoneCategories.CategoryOf("not_a_bone"));
    }

    [Fact]
    public void Order_is_the_tree_pre_order()
    {
        var order = KtisisBoneCategories.OrderById;
        Assert.Equal(KtisisBoneCategories.ById.Count, order.Count);
        Assert.Equal(0, order[KtisisBoneCategories.Roots[0].Id]);
        Assert.True(order["Head"] < order["Hair"]);
        Assert.True(order["Hair"] < order["Ears"]);
    }
}
