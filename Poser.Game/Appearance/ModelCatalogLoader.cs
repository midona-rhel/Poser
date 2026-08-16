using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Application.Appearance;
using Poser.Domain.Appearance;

namespace Poser.Game.Appearance;

/// <summary>
/// Builds the model-search catalog off the framework thread. One successful
/// build is retained for the lifetime of this loader. A failed build stays
/// failed until an explicit retry.
/// </summary>
public sealed class ModelCatalogLoader
{
    private readonly IDataManager _data;
    private readonly ModelCatalog _catalog;
    private readonly IPluginLog _log;
    private int _state;
    private string? _lastError;

    private const int Idle = 0;
    private const int Building = 1;
    private const int Loaded = 2;
    private const int Failed = 3;

    public ModelCatalogLoader(
        IDataManager data, ModelCatalog catalog, IPluginLog log)
    {
        _data = data;
        _catalog = catalog;
        _log = log;
    }

    public bool IsBuilding => Volatile.Read(ref _state) == Building;
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Starts one build for the current game data. Repeated calls
    /// while building or after success do nothing.</summary>
    public void EnsureLoaded()
    {
        if (Interlocked.CompareExchange(ref _state, Building, Idle) != Idle)
            return;
        StartBuild();
    }

    /// <summary>Retries a failed build after an explicit user action.</summary>
    public void Retry()
    {
        if (Interlocked.CompareExchange(ref _state, Building, Failed) != Failed)
            return;
        StartBuild();
    }

    private void StartBuild()
    {
        Task.Run(() =>
        {
            try
            {
                _catalog.Publish(Build());
                Volatile.Write(ref _lastError, null);
                Volatile.Write(ref _state, Loaded);
            }
            catch (Exception ex)
            {
                _log.Error($"Model catalog failed to build: {ex}");
                Volatile.Write(ref _lastError, ex.Message);
                Volatile.Write(ref _state, Failed);
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

    /// <summary>Joins event NPC models and names by row id.</summary>
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

    /// <summary>Formats a game-data singular name for display.</summary>
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
