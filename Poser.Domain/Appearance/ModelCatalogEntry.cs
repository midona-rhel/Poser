namespace Poser.Domain.Appearance;

/// <summary>
/// Which sheet a model-search row came from; also the search surface's kind
/// filter. Only kinds the game itself can NAME are admitted — battle NPCs
/// have no native base→name link (Brio bundles a LuminaSupplemental CSV for
/// them) and are deliberately absent until that dependency is accepted.
/// </summary>
public enum ModelCatalogKind
{
    EventNpc,
    Minion,
    Mount,
    Ornament,
}

/// <summary>
/// One model-search row: a named sheet row that draws as a concrete
/// ModelChara. <see cref="ModelCharaId"/> is the value the Model ID editor
/// applies, so a selected entry is directly applicable; rows whose model is
/// 0 (human event NPCs, whose look is customize data owned by Glamourer)
/// are never admitted.
/// </summary>
public sealed record ModelCatalogEntry(
    ModelCatalogKind Kind,
    uint RowId,
    string Name,
    uint Icon,
    int ModelCharaId);
