namespace Poser.Domain.Scene;

public enum WorldAssetKind { Scenery, Furniture, Effect }

/// <summary>
/// A spawnable game asset, including its file path and picker display text.
/// </summary>
public sealed record WorldAsset(
    string Name, string Path, string Label, string Context, uint IconId = 0)
{
    public static WorldAssetKind KindOf(string? path) =>
        path?.EndsWith(".sgb", System.StringComparison.OrdinalIgnoreCase) == true ? WorldAssetKind.Furniture
        : path?.EndsWith(".avfx", System.StringComparison.OrdinalIgnoreCase) == true ? WorldAssetKind.Effect
        : WorldAssetKind.Scenery;
}
