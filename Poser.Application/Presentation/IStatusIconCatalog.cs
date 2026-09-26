namespace Poser.Application.Presentation;

public readonly record struct StatusIconEntry(uint IconId, string Name);

public interface IStatusIconCatalog
{
    IReadOnlyList<StatusIconEntry> Entries { get; }
    string NameFor(uint iconId);
}
