using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Poser.Core;
using Poser.Services;
using FestivalRow = Lumina.Excel.Sheets.Festival;

namespace Poser.Game.Environment;

/// <summary>
/// Brio's FestivalService, with its mediator replaced by the event bus and its
/// toast by a log line. The eight engine slots are never written directly from
/// a caller: a change is queued and applied on a framework tick, and only while
/// the layout engine's festival status is 0 or 5 — applying one mid-transition
/// is what leaves the zone's festival layers half-loaded.
///
/// Festivals are the one environment control that IS restored. The pre-override
/// slots are snapshotted on the first mutation and written back on GPose exit
/// and on disposal; a territory change drops both the queue and the snapshot,
/// because the slots belong to the zone that is gone.
/// </summary>
public sealed unsafe class FestivalService : IFestivalService, IDisposable
{
    private const string FestivalDataResource = "Poser.Game.Data.Festivals.json";
    // The layout engine is only safe to write to between festival transitions.
    private const byte FestivalStatusIdle = 0;
    private const byte FestivalStatusReady = 5;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gpose;
    private readonly IEventBus _events;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    private readonly Queue<GameMain.Festival[]> _pending = new();
    private GameMain.Festival[]? _original;

    private readonly Dictionary<uint, FestivalEntry> _festivals = new();
    private readonly Dictionary<uint, AreaExclusion> _exclusions = new();

    public IReadOnlyDictionary<uint, FestivalEntry> FestivalList => _festivals;

    public bool HasOverride => _original != null;

    public bool CanModify => _gpose.IsGPosing;

    public FestivalService(
        IClientState clientState,
        IObjectTable objects,
        IFramework framework,
        IDataManager data,
        IPluginLog log,
        IGPoseService gpose,
        IEventBus events)
    {
        _clientState = clientState;
        _objects = objects;
        _framework = framework;
        _log = log;
        _gpose = gpose;
        _events = events;

        BuildFestivalList(data);

        _framework.Update += OnFrameworkUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _onGPoseStateChanged = OnGPoseStateChanged;
        _events.Subscribe(_onGPoseStateChanged);
    }

    // ── Engine slots ──────────────────────────────────────────────────

    private static GameMain.Festival[] EngineFestivals()
    {
        var slots = new GameMain.Festival[IFestivalService.MaxFestivals];
        var main = GameMain.Instance();
        if (main == null)
            return slots;
        var active = main->ActiveFestivals;
        for (var i = 0; i < IFestivalService.MaxFestivals; i++)
            slots[i] = active[i];
        return slots;
    }

    public IReadOnlyList<ActiveFestival> ActiveFestivals
        => EngineFestivals().Select(f => new ActiveFestival(f.Id, f.Phase)).ToList();

    public bool HasFreeSlot => EngineFestivals().Any(f => f.Id == 0);

    // ── Mutations ─────────────────────────────────────────────────────

    /// <summary>Mutations enqueue onto the framework tick's queue and read the
    /// engine's live slots, so they own both conditions the UI already checks
    /// before offering them: GPose, and the framework thread. Refusing here
    /// makes that structural rather than a UI convention.</summary>
    private bool CanMutate(string operation)
    {
        if (!CanModify)
        {
            _log.Warning($"FestivalService: {operation} requires GPose");
            return false;
        }
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Warning($"FestivalService: {operation} must run on the framework thread");
            return false;
        }
        return true;
    }

    public bool Add(uint id, ushort phase = 1)
    {
        if (!CanMutate(nameof(Add)))
            return false;
        if (!IsAllowedHere(id))
            return false;

        var active = EngineFestivals();
        for (var i = 0; i < IFestivalService.MaxFestivals; i++)
        {
            if (active[i].Id != 0)
                continue;
            SnapshotIfNeeded();
            active[i] = new GameMain.Festival { Id = (ushort)id, Phase = phase };
            _pending.Enqueue(active);
            return true;
        }

        return false;
    }

    public bool Remove(uint id)
    {
        if (!CanMutate(nameof(Remove)))
            return false;
        if (!IsAllowedHere(id))
            return false;

        var active = EngineFestivals();
        for (var i = 0; i < IFestivalService.MaxFestivals; i++)
        {
            if (active[i].Id != id)
                continue;
            SnapshotIfNeeded();
            active[i] = new GameMain.Festival { Id = 0, Phase = 0 };
            _pending.Enqueue(active);
            return true;
        }

        return false;
    }

    public bool ChangePhase(uint id, ushort phase)
    {
        if (!CanMutate(nameof(ChangePhase)))
            return false;

        var active = EngineFestivals();
        for (var i = 0; i < IFestivalService.MaxFestivals; i++)
        {
            if (active[i].Id != id)
                continue;
            SnapshotIfNeeded();
            active[i] = new GameMain.Festival { Id = (ushort)id, Phase = phase };
            _pending.Enqueue(active);
            return true;
        }

        // Phasing a festival that is not running means running it at that phase.
        return Add(id, phase);
    }

    public void Reset() => Restore(viaTick: true);

    /// <summary>Puts the snapshot back, either through the tick gate or — when
    /// no further tick will run — straight onto the engine's own queue.</summary>
    private void Restore(bool viaTick)
    {
        if (_original == null)
            return;
        if (viaTick)
            _pending.Enqueue([.. _original]);
        else
            Apply(_original, queueOnly: true);
        _original = null;
    }

    private void SnapshotIfNeeded() => _original ??= EngineFestivals();

    /// <summary>
    /// Writes the slots. The normal path sets them; the shutdown path can only
    /// queue them, because there is no further tick to wait for a safe layout
    /// state on. The pairing (0,4), (1,5), (2,6), (3,7) is the engine's
    /// argument order, not a slot reordering.
    /// </summary>
    private static void Apply(GameMain.Festival[] festivals, bool queueOnly)
    {
        var main = GameMain.Instance();
        if (main == null)
            return;

        if (queueOnly)
        {
            main->QueueActiveFestivals(
                festivals[0], festivals[4],
                festivals[1], festivals[5],
                festivals[2], festivals[6],
                festivals[3], festivals[7]);
        }
        else
        {
            main->SetActiveFestivals(
                festivals[0], festivals[4],
                festivals[1], festivals[5],
                festivals[2], festivals[6],
                festivals[3], festivals[7]);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_pending.Count == 0)
            return;

        var world = LayoutWorld.Instance();
        if (world == null)
            return;
        var layout = world->ActiveLayout;
        if (layout == null)
            return;
        if (layout->FestivalStatus != FestivalStatusReady && layout->FestivalStatus != FestivalStatusIdle)
            return;

        Apply(_pending.Dequeue(), queueOnly: false);
    }

    // ── Reference data ────────────────────────────────────────────────

    /// <summary>
    /// Every sheet row is offered. The curated file supplies names, phase names
    /// and the area exclusions; a row the file does not cover is still usable,
    /// flagged Unknown so the UI can say so rather than hide it.
    /// </summary>
    private void BuildFestivalList(IDataManager data)
    {
        var known = LoadFestivalFile();

        Lumina.Excel.ExcelSheet<FestivalRow>? sheet = null;
        try
        {
            sheet = data.GetExcelSheet<FestivalRow>();
        }
        catch (Exception ex)
        {
            _log.Warning($"Festivals: sheet unavailable ({ex.Message}); only the curated list is offered.");
        }

        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0)
                    continue;
                AddEntry(row.RowId, known.GetValueOrDefault(row.RowId));
            }
        }
        else
        {
            foreach (var (id, entry) in known)
            {
                if (id == 0)
                    continue;
                AddEntry(id, entry);
            }
        }
    }

    private void AddEntry(uint id, FestivalFileEntry? file)
    {
        if (file == null)
        {
            _festivals[id] = new FestivalEntry(id, "Unknown", true, false, []);
            return;
        }

        var phases = file.Phases?
            .Select(p => new FestivalPhaseInfo(p.Id, p.Name))
            .ToList() ?? [];
        _festivals[id] = new FestivalEntry(id, file.Name, false, file.Unsafe, phases);

        if (file.AreaExclusion is { } exclusion)
        {
            _exclusions[id] = new AreaExclusion(
                exclusion.Reason,
                exclusion.TerritoryType,
                [.. exclusion.Polygon.Select(p => new Vector2(p.X, p.Y))]);
        }
    }

    private Dictionary<uint, FestivalFileEntry> LoadFestivalFile()
    {
        var known = new Dictionary<uint, FestivalFileEntry>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(FestivalDataResource);
            if (stream == null)
            {
                _log.Warning($"Festivals: {FestivalDataResource} is missing; every festival will show as unknown.");
                return known;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                IncludeFields = true,
            };
            var entries = JsonSerializer.Deserialize<List<FestivalFileEntry>>(stream, options);
            if (entries == null)
                return known;
            foreach (var entry in entries)
                known[entry.Id] = entry;
        }
        catch (Exception ex)
        {
            _log.Warning($"Festivals: reference data failed to load ({ex.Message}); every festival will show as unknown.");
        }
        return known;
    }

    /// <summary>
    /// Some festivals break the zone when applied inside a specific interactive
    /// area. The exclusion is positional, so it is only refused where the player
    /// actually stands; a player that cannot be located refuses too.
    /// </summary>
    private bool IsAllowedHere(uint id)
    {
        if (!_exclusions.TryGetValue(id, out var exclusion))
            return true;
        if (_clientState.TerritoryType != exclusion.TerritoryType)
            return true;

        var player = _objects.LocalPlayer;
        if (player == null)
            return false;

        var position = new Vector2(player.Position.X, player.Position.Z);
        if (!IsPointInPolygon(position, exclusion.Polygon))
            return true;

        _log.Warning($"Festivals: {id} cannot be applied here — {exclusion.Reason}.");
        return false;
    }

    private static bool IsPointInPolygon(Vector2 point, Vector2[] polygon)
    {
        var inside = false;
        var j = polygon.Length - 1;
        for (var i = 0; i < polygon.Length; i++)
        {
            if (polygon[i].Y > point.Y != polygon[j].Y > point.Y &&
                point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    // ── Lifetime ──────────────────────────────────────────────────────

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
            Restore(viaTick: true);
    }

    private void OnTerritoryChanged(uint territory)
    {
        _pending.Clear();
        _original = null;
    }

    public void Dispose()
    {
        // No tick will run after this, so the restore goes to the engine queue.
        Restore(viaTick: false);

        _framework.Update -= OnFrameworkUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _events.Unsubscribe(_onGPoseStateChanged);

        _pending.Clear();
        _festivals.Clear();
        _exclusions.Clear();

        GC.SuppressFinalize(this);
    }

    private readonly record struct AreaExclusion(string Reason, ushort TerritoryType, Vector2[] Polygon);

    // Shapes of Data/Festivals.json, transcribed from the reference file.
    private sealed class FestivalFileEntry
    {
        public uint Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Unsafe { get; set; }
        public List<FestivalPhaseFile>? Phases { get; set; }
        public FestivalAreaExclusionFile? AreaExclusion { get; set; }
    }

    private sealed class FestivalPhaseFile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class FestivalAreaExclusionFile
    {
        public string Reason { get; set; } = string.Empty;
        public ushort TerritoryType { get; set; }
        public FestivalBoundaryFile[] Polygon { get; set; } = [];
    }

    private sealed class FestivalBoundaryFile
    {
        public float X { get; set; }
        public float Y { get; set; }
    }
}
