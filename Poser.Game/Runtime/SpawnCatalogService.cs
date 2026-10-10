using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Domain.Companions;
using Poser.Game.Companions;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Builds the spawn catalog from the Companion / Mount / Ornament sheets.
/// Minions and mounts need a non-zero id, model, and name. Ornaments share
/// the attachment catalog's action-string names and model-only admission.
///
/// <para>The build is LAZY and runs once. Singular names arrive lowercase from
/// the sheets ("wind-up cursor"), so the first character is raised and the rest
/// is left alone — the tail carries real casing.</para>
/// </summary>
public sealed class SpawnCatalogService : ISpawnCatalogService
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Func<uint, string> _ornamentName;
    private IReadOnlyList<SpawnCatalogEntry>? _entries;

    public SpawnCatalogService(IDataManager data, IPluginLog log, ISeStringEvaluator seStringEvaluator)
    {
        _data = data;
        _log = log;
        _ornamentName = id => seStringEvaluator.EvaluateActStr(ActionKind.Ornament, id);
    }

    public IReadOnlyList<SpawnCatalogEntry> Entries => _entries ??= Build();

    private List<SpawnCatalogEntry> Build()
    {
        var entries = new List<SpawnCatalogEntry>(1024);
        try
        {
            var companions = _data.GetExcelSheet<Companion>();
            if (companions != null)
                foreach (var row in companions)
                    if (row.RowId != 0 && row.Model.RowId != 0)
                        Add(
                            entries,
                            CompanionKind.Companion,
                            row.RowId,
                            row.Singular.ExtractText(),
                            row.Icon,
                            (int)row.Model.RowId);

            var mounts = _data.GetExcelSheet<Mount>();
            if (mounts != null)
                foreach (var row in mounts)
                    if (row.RowId != 0 && row.ModelChara.RowId != 0)
                        Add(
                            entries,
                            CompanionKind.Mount,
                            row.RowId,
                            row.Singular.ExtractText(),
                            (uint)row.Icon,
                            (int)row.ModelChara.RowId);

            var ornaments = _data.GetExcelSheet<Ornament>();
            if (ornaments != null)
                foreach (var row in ornaments)
                    if (OrnamentCatalogRows.Create(row.RowId, row.Model, row.Icon, _ornamentName) is { } entry)
                        entries.Add(new SpawnCatalogEntry(
                            entry.Kind, entry.Id, entry.Name, entry.Name.ToLowerInvariant(),
                            entry.Icon, (int)entry.ModelId));
        }
        catch (Exception ex)
        {
            // A partial catalog is still usable; an exception here must not
            // take the surface that asked for it down with it.
            _log.Error($"Spawn catalog failed to build: {ex}");
        }
        return entries;
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
