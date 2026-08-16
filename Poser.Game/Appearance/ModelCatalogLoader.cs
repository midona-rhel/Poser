using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Application.Appearance;
using Poser.Domain.Appearance;

namespace Poser.Game.Appearance;

/// <summary>
/// Builds the model-search catalog from game data, once per session, off
/// the framework thread — the CompanionCatalogLoader's lifecycle. The rows
/// are Brio's NpcSelector sources the game can NAME natively: event NPCs
/// (ENpcBase.ModelChara joined with ENpcResident's name by row id),
/// minions, mounts and ornaments (GameDataProvider.cs:56-60). Battle NPCs
/// are deliberately absent: their base→name link only exists in Brio's
/// bundled LuminaSupplemental CSV (GameDataProvider.cs:64-66), a new
/// package dependency that needs its own acceptance.
///
/// Admission is name + non-zero ModelChara: a human event NPC (model 0)
/// looks the way it does through customize data Glamourer owns, so it has
/// nothing for a model-id editor.
/// </summary>
public sealed class ModelCatalogLoader
{
    private readonly IDataManager _data;
    private readonly ModelCatalog _catalog;
    private readonly IPluginLog _log;
    private bool _started;

    public ModelCatalogLoader(
        IDataManager data, ModelCatalog catalog, IPluginLog log)
    {
        _data = data;
        _catalog = catalog;
        _log = log;
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
                _log.Error($"Model catalog failed to build: {ex}");
                _catalog.Publish(Array.Empty<ModelCatalogEntry>());
            }
        });
    }

    private List<ModelCatalogEntry> Build()
    {
        var entries = new List<ModelCatalogEntry>(4096);
        AddEventNpcs(entries);
        AddSheet<Companion>(entries, ModelCatalogKind.Minion,
            static row => (row.RowId, row.Singular.ExtractText(),
                row.Icon, (int)row.Model.RowId));
        AddSheet<Mount>(entries, ModelCatalogKind.Mount,
            static row => (row.RowId, row.Singular.ExtractText(),
                (uint)row.Icon, (int)row.ModelChara.RowId));
        AddSheet<Ornament>(entries, ModelCatalogKind.Ornament,
            static row => (row.RowId, row.Singular.ExtractText(),
                row.Icon, row.Model));
        return entries;
    }

    /// <summary>Event NPCs: the model comes from ENpcBase, the name from
    /// ENpcResident at the SAME row id — the sheets are parallel.</summary>
    private void AddEventNpcs(List<ModelCatalogEntry> entries)
    {
        var bases = _data.GetExcelSheet<ENpcBase>();
        var residents = _data.GetExcelSheet<ENpcResident>();
        if (bases == null || residents == null)
            return;

        var kindEntries = new List<ModelCatalogEntry>(1024);
        foreach (var row in bases)
        {
            if (row.RowId == 0 || row.ModelChara.RowId == 0)
                continue;
            var name = residents.GetRowOrDefault(row.RowId)?
                .Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            kindEntries.Add(new ModelCatalogEntry(
                ModelCatalogKind.EventNpc,
                row.RowId,
                Titled(name),
                Icon: 0,
                (int)row.ModelChara.RowId));
        }
        Append(entries, kindEntries);
    }

    private void AddSheet<TRow>(
        List<ModelCatalogEntry> entries,
        ModelCatalogKind kind,
        Func<TRow, (uint RowId, string Name, uint Icon, int ModelCharaId)> select)
        where TRow : struct, Lumina.Excel.IExcelRow<TRow>
    {
        var sheet = _data.GetExcelSheet<TRow>();
        if (sheet == null)
            return;

        var kindEntries = new List<ModelCatalogEntry>(1024);
        foreach (var row in sheet)
        {
            var (rowId, name, icon, modelCharaId) = select(row);
            if (rowId == 0 || modelCharaId == 0 || string.IsNullOrWhiteSpace(name))
                continue;
            kindEntries.Add(new ModelCatalogEntry(
                kind, rowId, Titled(name), icon, modelCharaId));
        }
        Append(entries, kindEntries);
    }

    /// <summary>Sheet names arrive lowercase from the Singular column; the
    /// catalog is the one place that knows the string came from game data.
    /// </summary>
    private static string Titled(string singular) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(singular);

    private static void Append(
        List<ModelCatalogEntry> entries, List<ModelCatalogEntry> kindEntries)
    {
        kindEntries.Sort(static (left, right) => string.Compare(
            left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        entries.AddRange(kindEntries);
    }
}
