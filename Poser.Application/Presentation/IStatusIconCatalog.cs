namespace Poser.Application.Presentation;

public readonly record struct StatusIconEntry(uint IconId, string Name);

public interface IStatusIconCatalog
{
    IReadOnlyList<StatusIconEntry> Entries { get; }
    string NameFor(uint iconId);
    /// <summary>Why reading the list from the game data failed, shown in the
    /// picker; null otherwise.</summary>
    string? LoadFailure { get; }
    /// <summary>Reads the list again after a failure; called when the picker opens.</summary>
    void Retry();
}
