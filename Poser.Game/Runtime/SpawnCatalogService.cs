using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Domain.Companions;
using Poser.Game.Companions;
using Poser.Application.Catalog;
using Poser.Game.Core;

namespace Poser.Game;

/// <summary>
/// Builds the spawn catalog from the Companion / Mount / Ornament sheets.
/// Minions and mounts need a non-zero id, model, and name. Ornaments share
/// the attachment catalog's action-string names and model-only admission.
///
/// <para>The list is read once, the first time it is asked for (see
/// <see cref="GameDataBuild"/>). Singular names arrive lowercase from
/// the sheets ("wind-up cursor"), so the first character is raised and the rest
/// is left alone — the tail carries real casing.</para>
/// </summary>
public sealed class SpawnCatalogService : ISpawnCatalogService
{
    private readonly IDataManager _data;
    private readonly Func<uint, string> _ornamentName;
    private readonly GameDataBuild _build;
    private IReadOnlyList<SpawnCatalogEntry> _entries = Array.Empty<SpawnCatalogEntry>();

    public SpawnCatalogService(IDataManager data, IPluginLog log, ISeStringEvaluator seStringEvaluator)
    {
        _data = data;
        _ornamentName = id => seStringEvaluator.EvaluateActStr(ActionKind.Ornament, id);
        _build = new("minion and mount", Build, log);
    }

    public IReadOnlyList<SpawnCatalogEntry> Entries
    {
        get { _build.BuildNow(); return _entries; }
    }

    public string? LoadFailure => _build.Failure;
    public void Retry() => _build.ForgetFailure();

    /// <summary>Publishes the list before filling it, so the rows read before
    /// a failing sheet stay listed next to the failure.</summary>
    private void Build()
    {
        var entries = new List<SpawnCatalogEntry>(1024);
        _entries = entries;
        foreach (var row in _data.GetExcelSheet<Companion>())
            if (row.RowId != 0 && row.Model.RowId != 0)
                Add(entries, CompanionKind.Companion, row.RowId,
                    row.Singular.ExtractText(), row.Icon, (int)row.Model.RowId);

        foreach (var row in _data.GetExcelSheet<Mount>())
            if (row.RowId != 0 && row.ModelChara.RowId != 0)
                Add(entries, CompanionKind.Mount, row.RowId,
                    row.Singular.ExtractText(), (uint)row.Icon, (int)row.ModelChara.RowId);

        foreach (var row in _data.GetExcelSheet<Ornament>())
            if (OrnamentCatalogRows.Create(row.RowId, row.Model, row.Icon, _ornamentName) is { } entry)
                entries.Add(new SpawnCatalogEntry(
                    entry.Kind, entry.Id, entry.Name, entry.Name.ToLowerInvariant(),
                    entry.Icon, (int)entry.ModelId));
    }

    /// <summary>Admission: the attachment id is a ushort, so a wider row can
    /// never be attached, and a nameless row can never be searched for.
    /// </summary>
    private static void Add(
        List<SpawnCatalogEntry> entries,
        CompanionKind kind,
        uint rowId,
        string name,
        uint iconId,
        int modelCharaId)
    {
        if (rowId > ushort.MaxValue || string.IsNullOrWhiteSpace(name))
            return;
        name = Capitalize(name);
        entries.Add(new SpawnCatalogEntry(
            kind, (ushort)rowId, name, name.ToLowerInvariant(), iconId,
            modelCharaId));
    }

    private static string Capitalize(string name) =>
        char.IsLower(name[0])
            ? char.ToUpperInvariant(name[0]) + name[1..]
            : name;
}
