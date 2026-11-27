namespace Poser.Domain.Companions;

/// <summary>A minion, mount or fashion accessory the spawn browser can spawn
/// as an actor, taken from the companion list.</summary>
public readonly record struct SpawnCatalogEntry(
    CompanionKind Kind, ushort Id, string Name, string NameLower, uint IconId, int ModelCharaId)
{
    /// <summary>The spawn browser's row for a companion list entry.</summary>
    public static SpawnCatalogEntry From(CompanionEntry entry) => new(
        entry.Kind, entry.Id, entry.Name, entry.Name.ToLowerInvariant(),
        entry.Icon, (int)entry.ModelId);
}
