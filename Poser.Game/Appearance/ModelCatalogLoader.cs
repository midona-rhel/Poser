using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Application.Appearance;
using Poser.Domain.Appearance;
using Poser.Application.Catalog;
using Poser.Game.Core;

namespace Poser.Game.Appearance;

/// <summary>
/// Builds the model-search catalog from game data, once, off the framework
/// thread (see <see cref="GameDataBuild"/>).
/// </summary>
public sealed class ModelCatalogLoader : IModelCatalogLoader
{
    private readonly IDataManager _data;
    private readonly ModelCatalog _catalog;
    private readonly GameDataBuild _build;

    public ModelCatalogLoader(
        IDataManager data, ModelCatalog catalog, IPluginLog log)
    {
        _data = data;
        _catalog = catalog;
        _build = new("model", () => _catalog.Publish(Build()), log);
    }

    public bool IsBuilding => _build.IsBuilding;
    public string? LastError => _build.Failure;
    public void EnsureLoaded() => _build.EnsureLoaded();
    public void Retry() => _build.Retry();

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
