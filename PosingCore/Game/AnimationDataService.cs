using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Provides access to game animation data from Excel sheets.
/// Loads lazily on first access to ensure game data is available.
/// </summary>
public class AnimationDataService : IAnimationDataService
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;
    private List<AnimationEntry>? _animations;
    private Dictionary<ushort, AnimationEntry>? _byId;
    private bool _loadAttempted;

    public IReadOnlyList<AnimationEntry> Animations
    {
        get
        {
            EnsureLoaded();
            return _animations ?? (IReadOnlyList<AnimationEntry>)Array.Empty<AnimationEntry>();
        }
    }

    public AnimationDataService(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    private void EnsureLoaded()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;

        _animations = new List<AnimationEntry>();
        _byId = new Dictionary<ushort, AnimationEntry>();

        try
        {
            LoadAnimations();
            _log.Debug($"AnimationDataService: Loaded {_animations.Count} animations");
        }
        catch (Exception ex)
        {
            _log.Warning($"AnimationDataService: Failed to load animation data: {ex.Message}");
        }
    }

    private void LoadAnimations()
    {
        var actionTimelines = _dataManager.GetExcelSheet<ActionTimeline>();
        var emotes = _dataManager.GetExcelSheet<Emote>();
        var actions = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();

        if (actionTimelines == null || emotes == null || actions == null)
            return;

        // Build a lookup for timeline keys
        var timelineKeys = new Dictionary<uint, string>();
        foreach (var timeline in actionTimelines)
        {
            var key = timeline.Key.ToString();
            if (!string.IsNullOrEmpty(key))
            {
                timelineKeys[timeline.RowId] = key;
            }
        }

        // Load emotes first (most commonly used)
        foreach (var emote in emotes)
        {
            var name = emote.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            // ActionTimeline array: [0]=Loop, [1]=Intro, [2]=Ground, [3]=Chair, [4]=UpperBody
            for (int i = 0; i < 5; i++)
            {
                var timelineRef = emote.ActionTimeline[i];
                if (timelineRef.RowId == 0)
                    continue;

                // Get the key from our lookup
                if (!timelineKeys.TryGetValue(timelineRef.RowId, out var key))
                    key = "";

                var suffix = i switch
                {
                    1 => " (Intro)",
                    2 => " (Ground)",
                    3 => " (Chair)",
                    4 => " (Blend)",
                    _ => ""
                };

                var entry = new AnimationEntry(
                    (ushort)timelineRef.RowId,
                    $"{name}{suffix}",
                    key,
                    AnimationCategory.Emote,
                    emote.Icon);

                AddEntry(entry);
            }
        }

        // Load actions
        foreach (var action in actions)
        {
            var name = action.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            if (action.AnimationEnd.RowId != 0)
            {
                // Get the key from our lookup
                if (!timelineKeys.TryGetValue(action.AnimationEnd.RowId, out var key))
                    key = "";

                var entry = new AnimationEntry(
                    (ushort)action.AnimationEnd.RowId,
                    name,
                    key,
                    AnimationCategory.Action,
                    action.Icon);

                AddEntry(entry);
            }
        }

        // Load raw timelines (ones without emote/action names)
        foreach (var timeline in actionTimelines)
        {
            var key = timeline.Key.ToString();
            if (string.IsNullOrEmpty(key))
                continue;

            // Skip if we already have this ID from emotes/actions
            if (_byId!.ContainsKey((ushort)timeline.RowId))
                continue;

            var entry = new AnimationEntry(
                (ushort)timeline.RowId,
                key, // Use key as name for raw timelines
                key,
                AnimationCategory.Raw,
                0); // No icon for raw timelines

            AddEntry(entry);
        }

        // Sort: Emotes first, then Actions, then Raw, alphabetically within each
        _animations!.Sort((a, b) =>
        {
            if (a.Category != b.Category)
                return a.Category.CompareTo(b.Category);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void AddEntry(AnimationEntry entry)
    {
        // Only keep the first entry for each ID (prefer emote names over raw)
        if (!_byId!.ContainsKey(entry.TimelineId))
        {
            _animations!.Add(entry);
            _byId[entry.TimelineId] = entry;
        }
    }

    public IEnumerable<AnimationEntry> Search(string query, int maxResults = 50)
    {
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(query))
            return _animations!.Take(maxResults);

        // Check if query is a number (ID search)
        if (ushort.TryParse(query, out var id))
        {
            if (_byId!.TryGetValue(id, out var exact))
                return new[] { exact };

            // Partial ID match
            return _animations!
                .Where(a => a.TimelineId.ToString().Contains(query))
                .Take(maxResults);
        }

        // Text search
        return _animations!
            .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       a.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults);
    }

    public AnimationEntry? GetById(ushort timelineId)
    {
        EnsureLoaded();
        return _byId!.TryGetValue(timelineId, out var entry) ? entry : null;
    }
}
