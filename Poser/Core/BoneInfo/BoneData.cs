namespace Poser.Core.BoneInfo;

/// <summary>
/// Data for a single bone: translation and category.
/// </summary>
public readonly record struct BoneData(string Translation, BoneCategory Category);
