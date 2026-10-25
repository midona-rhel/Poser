namespace Poser.Domain.Appearance;

/// <summary>
/// Categories available in model search. Battle NPCs are not included in
/// this catalog; their model-to-name lookup needs a separate data source.
/// </summary>
public enum ModelCatalogKind
{
    EventNpc,
    Minion,
    Mount,
    Ornament,
}

/// <summary>
/// A model-search result. RowId identifies its game-data entry;
/// ModelCharaId is the model number applied by the Model ID editor.
/// </summary>
public sealed record ModelCatalogEntry(
    ModelCatalogKind Kind,
    uint RowId,
    string Name,
    uint Icon,
    int ModelCharaId);
