namespace Poser.Services;

/// <summary>
/// One spawnable game asset: the path the spawn takes, the LABEL a person
/// searches by — Brio's derived naming, "Type [stem]" — and the context
/// line (expansion · subtype) the picker badges.
/// </summary>
public sealed record WorldAsset(
    string Name, string Path, string Label, string Context);
