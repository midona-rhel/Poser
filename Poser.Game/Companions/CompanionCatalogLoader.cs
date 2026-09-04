using System;
using Poser.Services;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Application.Companions;
using Poser.Domain.Companions;

namespace Poser.Game.Companions;

/// <summary>
/// Builds the companion catalog from game data, once per session, off the
/// framework thread. Reads three sheets:
/// Companion (minions, keyed by Model), Mount (keyed by ModelChara) and
/// Ornament (keyed by a plain Model value, not a row reference).
///
/// Minions and mounts require a name and non-zero model. Ornaments use
/// action-string names, with an ID fallback for unnamed modelled rows.
///
/// Sheet names are the Singular column and arrive lowercase, so they are
/// title-cased here rather than in the UI — the catalog is the one place
/// that knows the string came from game data.
/// </summary>
public sealed class CompanionCatalogLoader : ICompanionCatalogLoader
{
    private readonly IDataManager _data;
    private readonly CompanionCatalog _catalog;
    private readonly IPluginLog _log;
    private readonly Func<uint, string> _ornamentName;
    private bool _started;

    public CompanionCatalogLoader(
        IDataManager data, CompanionCatalog catalog, IPluginLog log,
        ISeStringEvaluator seStringEvaluator)
    {
        _data = data;
        _catalog = catalog;
        _log = log;
        _ornamentName = id => seStringEvaluator.EvaluateActStr(ActionKind.Ornament, id);
    }

    /// <summary>Starts the one-time build. Safe to call repeatedly.</summary>
    public void EnsureLoaded()
    {
        if (_started)
            return;
        _started = true;
        Task.Run(() =>
        {
            try
            {
                _catalog.Publish(Build());
            }
            catch (Exception ex)
            {
                _log.Error($"Companion catalog failed to build: {ex}");
                _catalog.Publish(Array.Empty<CompanionEntry>());
            }
        });
    }

    private List<CompanionEntry> Build()
    {
        var entries = new List<CompanionEntry>(1024);
        AddCompanions(entries);
        AddMounts(entries);
        AddOrnaments(entries);
        return entries;
    }

    private void AddCompanions(List<CompanionEntry> entries)
    {
        var sheet = _data.GetExcelSheet<Companion>();
        if (sheet == null)
            return;

        var kindEntries = new List<CompanionEntry>(sheet.Count);
        foreach (var row in sheet)
        {
            if (!TryIdentify(row.RowId, row.Singular.ExtractText(), row.Model.RowId, out var id, out var name))
                continue;
            kindEntries.Add(new CompanionEntry(
                CompanionKind.Companion, id, name, row.Icon, row.Model.RowId));
        }
        Append(entries, kindEntries);
    }

    private void AddMounts(List<CompanionEntry> entries)
    {
        var sheet = _data.GetExcelSheet<Mount>();
        if (sheet == null)
            return;

        var kindEntries = new List<CompanionEntry>(sheet.Count);
        foreach (var row in sheet)
        {
            if (!TryIdentify(row.RowId, row.Singular.ExtractText(), row.ModelChara.RowId, out var id, out var name))
                continue;
            kindEntries.Add(new CompanionEntry(
                CompanionKind.Mount, id, name, row.Icon, row.ModelChara.RowId));
        }
        Append(entries, kindEntries);
    }

    private void AddOrnaments(List<CompanionEntry> entries)
    {
        var sheet = _data.GetExcelSheet<Ornament>();
        if (sheet == null)
            return;

        var kindEntries = new List<CompanionEntry>(sheet.Count);
        foreach (var row in sheet)
        {
            if (OrnamentCatalogRows.Create(row.RowId, row.Model, row.Icon, _ornamentName) is { } entry)
                kindEntries.Add(entry);
        }
        Append(entries, kindEntries);
    }

    /// <summary>
    /// The shared admission test: a named row with a model, whose id still
    /// fits the ushort the native companion container takes.
    /// </summary>
    private static bool TryIdentify(
        uint rowId, string singular, uint modelId, out ushort id, out string name)
    {
        id = 0;
        name = string.Empty;
        if (rowId == 0 || rowId > ushort.MaxValue)
            return false;
        if (modelId == 0)
            return false;
        if (string.IsNullOrWhiteSpace(singular))
            return false;
        id = (ushort)rowId;
        name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(singular);
        return true;
    }

    private static void Append(List<CompanionEntry> entries, List<CompanionEntry> kindEntries)
    {
        kindEntries.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        entries.AddRange(kindEntries);
    }
}
