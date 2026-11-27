using Poser.Domain.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using FestivalRow = Lumina.Excel.Sheets.Festival;

namespace Poser.Game.Environment;

/// <summary>
/// Adds, removes and changes the seasonal festivals the area shows, and puts
/// the area's own festivals back when the player leaves GPose.
///
/// The game has eight festival slots. A change is not written at once: like
/// Brio, Poser waits until the game is not in the middle of switching
/// festivals, to avoid writing into a switch that is still running. The
/// slots waiting to be written are kept as one set, and every change starts
/// from that set, so two changes in a row both arrive.
///
/// The first change remembers the area's own festivals. Reset, and leaving
/// GPose, make those the set waiting to be written.
/// </summary>
public sealed unsafe class FestivalService : IDisposable
{
    /// <summary>The game has eight festival slots.</summary>
    public const int MaxFestivals = 8;
    private const string FestivalDataResource = "Poser.Game.Data.Festivals.json";
    // The game can take festival changes while its festival status is one of these.
    private const byte FestivalStatusIdle = 0;
    private const byte FestivalStatusReady = 5;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gpose;
    private readonly IEventBus _events;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    // The slots waiting to be written; null when nothing is waiting.
    private GameMain.Festival[]? _pending;
    // The area's own festivals, remembered at Poser's first change.
    private GameMain.Festival[]? _original;

    private readonly Dictionary<uint, FestivalEntry> _festivals = new();
    private readonly Dictionary<uint, AreaExclusion> _exclusions = new();

    /// <summary>Every festival in the game data, by id.</summary>
    public IReadOnlyDictionary<uint, FestivalEntry> FestivalList => _festivals;

    /// <summary>True when Poser has changed the festivals and Reset has
    /// something to put back.</summary>
    public bool HasOverride => _original != null;

    /// <summary>Festivals can only be changed in GPose.</summary>
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
        _onGPoseStateChanged = OnGPoseStateChanged;
        _events.Subscribe(_onGPoseStateChanged);
    }

    // ── Slots ─────────────────────────────────────────────────────────

    /// <summary>A copy of the slots as they will be once nothing is waiting:
    /// the waiting set if there is one, otherwise what the game shows.</summary>
    private GameMain.Festival[] Slots()
    {
        if (_pending != null)
            return [.. _pending];
        var slots = new GameMain.Festival[MaxFestivals];
        var active = GameMain.Instance()->ActiveFestivals;
        for (var i = 0; i < MaxFestivals; i++)
            slots[i] = active[i];
        return slots;
    }

    /// <summary>All eight slots, empty ones included, as they will be once
    /// the waiting changes are written.</summary>
    public IReadOnlyList<ActiveFestival> ActiveFestivals
        => Slots().Select(f => new ActiveFestival(f.Id, f.Phase)).ToList();

    public bool HasFreeSlot => Slots().Any(f => f.Id == 0);

    // ── Changes ───────────────────────────────────────────────────────

    /// <summary>Puts a festival into the first empty slot. False outside
    /// GPose, when no slot is free, or where the festival isn't allowed.</summary>
    public bool Add(uint id, ushort phase = 1) =>
        CanModify && IsAllowedHere(id) &&
        Write(slot => slot.Id == 0, new GameMain.Festival { Id = (ushort)id, Phase = phase });

    /// <summary>Empties the slot that holds the festival. False outside GPose,
    /// when it isn't running, or where the festival isn't allowed.</summary>
    public bool Remove(uint id) =>
        CanModify && IsAllowedHere(id) &&
        Write(slot => slot.Id == id, new GameMain.Festival { Id = 0, Phase = 0 });

    /// <summary>Changes the stage a running festival shows, or adds the
    /// festival at that stage when it isn't running.</summary>
    public bool ChangePhase(uint id, ushort phase) =>
        CanModify &&
        (Write(slot => slot.Id == id, new GameMain.Festival { Id = (ushort)id, Phase = phase }) ||
            Add(id, phase));

    /// <summary>Writes <paramref name="value"/> into the first slot that
    /// matches, as the new waiting set.</summary>
    private bool Write(Func<GameMain.Festival, bool> match, GameMain.Festival value)
    {
        var slots = Slots();
        for (var i = 0; i < MaxFestivals; i++)
        {
            if (!match(slots[i]))
                continue;
            // The set this change starts from is the area's own festivals
            // when Poser hasn't changed them yet.
            _original ??= Slots();
            slots[i] = value;
            _pending = slots;
            return true;
        }
        return false;
    }

    /// <summary>Makes these eight slots the set waiting to be written. Undoing
    /// Reset uses it to put every slot back where it was.</summary>
    public void SetSlots(IReadOnlyList<ActiveFestival> slots)
    {
        if (!CanModify)
            return;
        _original ??= Slots();
        _pending = slots.Select(s => new GameMain.Festival { Id = (ushort)s.Id, Phase = s.Phase }).ToArray();
    }

    /// <summary>Puts back the area's own festivals.</summary>
    public void Reset()
    {
        if (_original == null)
            return;
        _pending = _original;
        _original = null;
    }

    /// <summary>
    /// Writes the eight slots in their order: <c>festivals[0]</c> fills slot
    /// 0, <c>festivals[1]</c> slot 1, and so on. Brio passes them as (0,4),
    /// (1,5), (2,6), (3,7), which puts slot 4 into slot 1, slot 1 into slot
    /// 2 and so on, so festivals outside slots 0 and 7 move with each write.
    /// Setting them shows them at once. Unloading Poser queues them instead,
    /// because no later frame runs to wait for the game to finish a switch.
    /// </summary>
    private static void Apply(GameMain.Festival[] festivals, bool queueOnly)
    {
        var main = GameMain.Instance();
        if (queueOnly)
        {
            main->QueueActiveFestivals(
                festivals[0], festivals[1], festivals[2], festivals[3],
                festivals[4], festivals[5], festivals[6], festivals[7]);
        }
        else
        {
            main->SetActiveFestivals(
                festivals[0], festivals[1], festivals[2], festivals[3],
                festivals[4], festivals[5], festivals[6], festivals[7]);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_pending == null)
            return;
        // The layout is missing while the game is loading an area.
        var layout = LayoutWorld.Instance()->ActiveLayout;
        if (layout == null)
            return;
        if (layout->FestivalStatus != FestivalStatusReady && layout->FestivalStatus != FestivalStatusIdle)
            return;

        Apply(_pending, queueOnly: false);
        _pending = null;
    }

    // ── Festival list ─────────────────────────────────────────────────

    /// <summary>
    /// Every festival in the game data is offered. Brio's festival file, built
    /// into Poser, gives the names, the stage names and the places where a
    /// festival is not allowed. A festival the file doesn't know is still
    /// offered, named Unknown.
    /// </summary>
    private void BuildFestivalList(IDataManager data)
    {
        var known = LoadFestivalFile();
        foreach (var row in data.GetExcelSheet<FestivalRow>())
        {
            if (row.RowId != 0)
                AddEntry(row.RowId, known.GetValueOrDefault(row.RowId));
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

    private static Dictionary<uint, FestivalFileEntry> LoadFestivalFile()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(FestivalDataResource)!;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
        };
        return JsonSerializer.Deserialize<List<FestivalFileEntry>>(stream, options)!
            .ToDictionary(entry => entry.Id);
    }

    /// <summary>
    /// Some festivals break the area when they are shown inside one marked
    /// part of it. They are refused only while the player stands inside that
    /// part.
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

    // Leaving GPose always comes before a zone change, so the area's own
    // festivals are waiting to be written by then and are written in the new
    // area as soon as it has loaded.
    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
            Reset();
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _events.Unsubscribe(_onGPoseStateChanged);
        // No frame runs after this, so the area's own festivals are queued for
        // the game to apply.
        if ((_original ?? _pending) is { } own)
            Apply(own, queueOnly: true);
    }

    private readonly record struct AreaExclusion(string Reason, ushort TerritoryType, Vector2[] Polygon);

    // The shape of Data/Festivals.json, which comes from Brio.
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
