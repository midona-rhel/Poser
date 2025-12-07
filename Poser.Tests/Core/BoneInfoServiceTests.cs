using Poser.Core.BoneInfo;
using Xunit;

namespace Poser.Tests.Core;

public class BoneInfoServiceTests
{
    [Theory]
    [InlineData(BoneCategory.Root, "n_root")]
    [InlineData(BoneCategory.Spine, "j_kosi")]
    [InlineData(BoneCategory.Head, "j_kao")]
    [InlineData(BoneCategory.LeftArm, "j_ude_a_l")]
    [InlineData(BoneCategory.RightArm, "j_ude_a_r")]
    [InlineData(BoneCategory.LeftLeg, "j_asi_a_l")]
    [InlineData(BoneCategory.RightLeg, "j_asi_a_r")]
    [InlineData(BoneCategory.Tail, "j_sippo_a")]
    public void GetCategoryRootBone_ReturnsCorrectBone(BoneCategory category, string expectedBone)
    {
        var result = BoneInfoService.GetCategoryRootBone(category);
        Assert.Equal(expectedBone, result);
    }

    [Theory]
    [InlineData(BoneCategory.Equipment)]
    [InlineData(BoneCategory.Other)]
    public void GetCategoryRootBone_ReturnsNullForAbstractCategories(BoneCategory category)
    {
        var result = BoneInfoService.GetCategoryRootBone(category);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(BoneSubcategory.Hair, "j_kami_a")]
    [InlineData(BoneSubcategory.Ears, "j_mimi_l")]
    [InlineData(BoneSubcategory.LeftEye, "j_f_eye_l")]
    [InlineData(BoneSubcategory.RightEye, "j_f_eye_r")]
    [InlineData(BoneSubcategory.Mouth, "j_ago")]
    public void GetSubcategoryRootBone_ReturnsCorrectBone(BoneSubcategory subcategory, string expectedBone)
    {
        var result = BoneInfoService.GetSubcategoryRootBone(subcategory);
        Assert.Equal(expectedBone, result);
    }

    [Theory]
    [InlineData(BoneSubcategory.None)]
    [InlineData(BoneSubcategory.Face)]
    [InlineData(BoneSubcategory.Eyebrows)]
    [InlineData(BoneSubcategory.Nose)]
    [InlineData(BoneSubcategory.Cheeks)]
    [InlineData(BoneSubcategory.Hand)]
    [InlineData(BoneSubcategory.Fingers)]
    [InlineData(BoneSubcategory.Foot)]
    [InlineData(BoneSubcategory.Toes)]
    public void GetSubcategoryRootBone_ReturnsNullForAbstractSubcategories(BoneSubcategory subcategory)
    {
        var result = BoneInfoService.GetSubcategoryRootBone(subcategory);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(BoneCategory.Root, "Root")]
    [InlineData(BoneCategory.Head, "Head")]
    [InlineData(BoneCategory.Spine, "Spine")]
    [InlineData(BoneCategory.LeftArm, "Left Arm")]
    [InlineData(BoneCategory.RightArm, "Right Arm")]
    [InlineData(BoneCategory.LeftLeg, "Left Leg")]
    [InlineData(BoneCategory.RightLeg, "Right Leg")]
    [InlineData(BoneCategory.Tail, "Tail")]
    [InlineData(BoneCategory.Equipment, "Equipment")]
    [InlineData(BoneCategory.Other, "Other")]
    public void GetCategoryDisplayName_ReturnsCorrectName(BoneCategory category, string expectedName)
    {
        var result = BoneInfoService.GetCategoryDisplayName(category);
        Assert.Equal(expectedName, result);
    }

    [Theory]
    [InlineData(BoneSubcategory.None, "")]
    [InlineData(BoneSubcategory.Face, "Face")]
    [InlineData(BoneSubcategory.LeftEye, "Left Eye")]
    [InlineData(BoneSubcategory.RightEye, "Right Eye")]
    [InlineData(BoneSubcategory.Eyebrows, "Eyebrows")]
    [InlineData(BoneSubcategory.Nose, "Nose")]
    [InlineData(BoneSubcategory.Mouth, "Mouth")]
    [InlineData(BoneSubcategory.Cheeks, "Cheeks")]
    [InlineData(BoneSubcategory.Hair, "Hair")]
    [InlineData(BoneSubcategory.Ears, "Ears")]
    [InlineData(BoneSubcategory.Hand, "Hand")]
    [InlineData(BoneSubcategory.Fingers, "Fingers")]
    [InlineData(BoneSubcategory.Foot, "Foot")]
    [InlineData(BoneSubcategory.Toes, "Toes")]
    public void GetSubcategoryDisplayName_ReturnsCorrectName(BoneSubcategory subcategory, string expectedName)
    {
        var result = BoneInfoService.GetSubcategoryDisplayName(subcategory);
        Assert.Equal(expectedName, result);
    }
}
