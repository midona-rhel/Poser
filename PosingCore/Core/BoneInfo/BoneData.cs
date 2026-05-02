namespace Poser.Core.BoneInfo;

/// <summary>
/// Data for a single bone: translation, category, and optional subcategory.
/// </summary>
public readonly record struct BoneData(string Translation, BoneCategory Category, BoneSubcategory Subcategory = BoneSubcategory.None);
