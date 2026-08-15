using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Poser.Game.Overlays;

/// <summary>One pickable status icon: the sheet's own name over the icon id
/// the node draws.</summary>
public readonly record struct StatusIconEntry(uint IconId, string Name);

/// <summary>
/// Every status effect the game declares, as an icon the user can put on a
/// staged status line. Built ONCE per session and lazily — a session that
/// never opens the icon picker never walks the sheet.
///
/// <para>Deduplicated by ICON, not by row: the sheet states one row per stack
/// count and per variant, and a picker that listed all of them would show the
/// same picture forty times (Ktisis dedupes the same way,
/// <c>Interface/Editor/Properties/OverlayPropertyList.cs:53</c>).</para>
/// </summary>
public sealed class StatusIconCatalog
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private IReadOnlyList<StatusIconEntry>? _entries;

    public StatusIconCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyList<StatusIconEntry> Entries => _entries ??= Build();

    private IReadOnlyList<StatusIconEntry> Build()
    {
        try
        {
            var sheet = _data.GetExcelSheet<Status>();
            if (sheet == null)
                return Array.Empty<StatusIconEntry>();
            var seen = new HashSet<uint>();
            var entries = new List<StatusIconEntry>(sheet.Count);
            foreach (var row in sheet)
            {
                if (row.Icon == 0 || row.Name.IsEmpty)
                    continue;
                if (!seen.Add(row.Icon))
                    continue;
                string name = row.Name.ExtractText();
                if (name.Length == 0)
                    continue;
                entries.Add(new StatusIconEntry(row.Icon, name));
            }
            entries.Sort(static (a, b) => string.Compare(
                a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return entries;
        }
        catch (Exception ex)
        {
            _log.Error($"StatusIconCatalog: the status sheet failed: {ex.Message}");
            return Array.Empty<StatusIconEntry>();
        }
    }

    /// <summary>The listed name for one icon id, or an empty string when the
    /// running client declares none.</summary>
    public string NameFor(uint iconId)
    {
        foreach (var entry in Entries)
            if (entry.IconId == iconId)
                return entry.Name;
        return string.Empty;
    }
}
