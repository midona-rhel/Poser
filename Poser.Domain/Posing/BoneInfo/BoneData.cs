namespace Poser.Domain.Posing.BoneInfo;

/// <summary>The curated display name for one bone. Grouping comes from
/// <see cref="Poser.Domain.Posing.KtisisBoneCategories"/>, the one tree the
/// sidebar, matrix and presets share.</summary>
public readonly record struct BoneData(string Translation);
