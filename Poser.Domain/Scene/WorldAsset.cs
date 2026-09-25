namespace Poser.Services;

public enum WorldAssetKind { Scenery, Furniture, Effect }

/// <summary>
/// One spawnable game asset: the path the spawn takes, the LABEL a person
/// searches by — Brio's derived naming, "Type [stem]" — and the context
/// line (expansion · subtype) the picker badges.
/// </summary>
public sealed record WorldAsset(
    string Name, string Path, string Label, string Context, uint IconId = 0)
{
    public static WorldAssetKind KindOf(string? path) =>
        path?.EndsWith(".sgb", System.StringComparison.OrdinalIgnoreCase) == true ? WorldAssetKind.Furniture
        : path?.EndsWith(".avfx", System.StringComparison.OrdinalIgnoreCase) == true ? WorldAssetKind.Effect
        : WorldAssetKind.Scenery;
}
