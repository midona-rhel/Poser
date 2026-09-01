using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Poser.Game;

/// <summary>One of the game's dyes: the stain row's id and its own name.
/// </summary>
public sealed record StainEntry(byte Id, string Name);

/// <summary>
/// The game's dye sheet, flat: what a prop's two stain channels choose
/// from. Row zero is the undyed state and is stated as such rather than
/// dropped — clearing a dye is a choice like any other.
/// </summary>
public sealed class StainCatalog
{
    private readonly IDataManager _data;
    private IReadOnlyList<StainEntry>? _entries;

    public StainCatalog(IDataManager data) => _data = data;

    public IReadOnlyList<StainEntry> Entries => _entries ??= Load();

    /// <summary>The dye's name, or the undyed/unknown statement.</summary>
    public string NameOf(byte id)
    {
        if (id == 0)
            return "None";
        foreach (var entry in Entries)
            if (entry.Id == id)
                return entry.Name;
        return "Dye " + id;
    }

    private IReadOnlyList<StainEntry> Load()
    {
        var entries = new List<StainEntry> { new(0, "None") };
        try
        {
            var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Stain>();
            if (sheet != null)
                foreach (var row in sheet)
                {
                    if (row.RowId == 0 || row.RowId > byte.MaxValue)
                        continue;
                    string name = row.Name.ExtractText();
                    if (name.Length == 0)
                        continue;
                    entries.Add(new StainEntry((byte)row.RowId, name));
                }
        }
        catch (Exception)
        {
            // The sheet failing to read leaves "None" alone — the pickers
            // then say honestly that there is nothing to choose.
        }
        return entries;
    }
}
