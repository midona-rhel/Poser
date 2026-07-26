using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Application.Animation;
using Poser.Domain.Animation;

namespace Poser.Game.Animation;

/// <summary>
/// Builds the animation catalog from game data, once per session, off the
/// framework thread. Reads three sheets, exactly as Ktisis does:
/// Emote (one entry per valid timeline the emote references, so an
/// emote's intro and loop are separately playable), Action (deduplicated
/// by name/icon/animation because the sheet repeats every action per job
/// and rank), and ActionTimeline (raw rows with a key).
///
/// The admission rules are the filter: an entry only exists if it has a
/// name, a non-zero timeline, and a slot the runtime is willing to write.
/// Nothing that reaches the UI can therefore fail after selection.
///
/// The slot comes from the sheet's STANCE column, as Ktisis derives it in
/// all three entry types. The sheet also has a column literally named
/// Slot, and it is the wrong one: deriving from it left every facial
/// timeline unclassified, so the Expression picker found nothing and a
/// facial-layer pick offered timelines the sequencer then ignored.
/// </summary>
public sealed class AnimationCatalogLoader
{
    private readonly IDataManager _data;
    private readonly AnimationCatalog _catalog;
    private readonly IPluginLog _log;
    private bool _started;

    public AnimationCatalogLoader(
        IDataManager data, AnimationCatalog catalog, IPluginLog log)
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
                _log.Error($"Animation catalog failed to build: {ex}");
                _catalog.Publish(Array.Empty<TimelineEntry>());
            }
        });
    }

    private List<TimelineEntry> Build()
    {
        var entries = new List<TimelineEntry>(4096);
        var timelines = _data.GetExcelSheet<ActionTimeline>();
        if (timelines == null)
            return entries;

        AddEmotes(entries, timelines);
        AddActions(entries);
        AddRawTimelines(entries, timelines);
        return entries;
    }

    private void AddEmotes(List<TimelineEntry> entries, Lumina.Excel.ExcelSheet<ActionTimeline> timelines)
    {
        var emotes = _data.GetExcelSheet<Emote>();
        if (emotes == null)
            return;

        // Distinct on (name, slot): the sheet repeats the same emote across
        // several timeline indices that land in the same slot, and showing
        // each one separately is noise rather than choice.
        var seen = new HashSet<(string, AnimationSlot)>();
        foreach (var emote in emotes)
        {
            var name = emote.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            // EmoteCategory 3 is the game's own "expression" grouping.
            bool isExpression = emote.EmoteCategory.RowId == 3;

            for (int index = 0; index < emote.ActionTimeline.Count; index++)
            {
                var reference = emote.ActionTimeline[index];
                if (!reference.IsValid || reference.RowId == 0)
                    continue;
                if (!TryResolveSlot(timelines, reference.RowId, out var slot))
                    continue;
                if (!seen.Add((name, slot)))
                    continue;
                entries.Add(new TimelineEntry(
                    reference.RowId,
                    name,
                    isExpression ? AnimationKind.Expression : AnimationKind.Emote,
                    slot,
                    emote.Icon,
                    emote.RowId,
                    index,
                    // Only emotes know their weapon state; actions and raw
                    // timelines stay null and pass Brio's drawn filter.
                    emote.DrawsWeapon));
            }
        }
    }

    private void AddActions(List<TimelineEntry> entries)
    {
        var actions = _data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (actions == null)
            return;

        var seen = new HashSet<(string, ushort, uint)>();
        foreach (var action in actions)
        {
            var name = action.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            // The playable timeline is the action's END animation; the
            // start is the wind-up and is the sheet's dedupe key.
            if (!action.AnimationEnd.IsValid || action.AnimationEnd.RowId == 0)
                continue;
            if (!seen.Add((name, action.Icon, action.AnimationStart.RowId)))
                continue;

            var row = action.AnimationEnd.Value;
            if (!AnimationSlots.IsKnown(row.Stance))
                continue;
            entries.Add(new TimelineEntry(
                action.AnimationEnd.RowId,
                name,
                AnimationKind.Action,
                (AnimationSlot)row.Stance,
                action.Icon));
        }
    }

    private static void AddRawTimelines(
        List<TimelineEntry> entries, Lumina.Excel.ExcelSheet<ActionTimeline> timelines)
    {
        foreach (var timeline in timelines)
        {
            var key = timeline.Key.ExtractText();
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (!AnimationSlots.IsKnown(timeline.Stance))
                continue;
            entries.Add(new TimelineEntry(
                timeline.RowId,
                key,
                AnimationKind.RawTimeline,
                (AnimationSlot)timeline.Stance));
        }
    }

    private static bool TryResolveSlot(
        Lumina.Excel.ExcelSheet<ActionTimeline> timelines, uint rowId, out AnimationSlot slot)
    {
        slot = AnimationSlot.Base;
        var row = timelines.GetRowOrDefault(rowId);
        if (row == null || !AnimationSlots.IsKnown(row.Value.Stance))
            return false;
        slot = (AnimationSlot)row.Value.Stance;
        return true;
    }
}
