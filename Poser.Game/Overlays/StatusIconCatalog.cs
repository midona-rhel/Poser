using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Application.Presentation;
using Poser.Game.Core;

namespace Poser.Game.Overlays;

/// <summary>
/// Provides the game's status-effect icons for staged status lines. The sheet
/// is read when the icon picker first asks for it (see <see cref="GameDataBuild"/>).
///
/// <para>Deduplicated by icon, not by row: the sheet contains one row per stack
/// count and per variant, and a picker that listed all of them would show the
/// same picture forty times (Ktisis dedupes the same way,
/// <c>Interface/Editor/Properties/OverlayPropertyList.cs:53</c>).</para>
/// </summary>
public sealed class StatusIconCatalog : IStatusIconCatalog
{
    private readonly IDataManager _data;
    private readonly GameDataBuild _build;
    private IReadOnlyList<StatusIconEntry> _entries = Array.Empty<StatusIconEntry>();

    public StatusIconCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _build = new("status icon", () => _entries = Build(), log);
    }

    public IReadOnlyList<StatusIconEntry> Entries
    {
        get { _build.BuildNow(); return _entries; }
    }

    public string? LoadFailure => _build.Failure;
    public void Retry() => _build.ForgetFailure();

    private List<StatusIconEntry> Build()
    {
        var sheet = _data.GetExcelSheet<Status>();
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
