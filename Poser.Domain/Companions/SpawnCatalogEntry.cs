namespace Poser.Domain.Companions;

/// <summary>Immutable game-catalog facts; sheet reading stays in the runtime.</summary>
public readonly record struct SpawnCatalogEntry(
    CompanionKind Kind, ushort Id, string Name, string NameLower, uint IconId, int ModelCharaId);
