using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Game.Types;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Binder for <see cref="SpawnBrowserView"/> (view+binder pattern —
/// docs/architecture/ui-workspace.md): owns the flat row list, the filter
/// cache, the footer caption and every spawn/attach call the rows make.
///
/// <para>ONE surface answers "add something to the scene": the four creation
/// actions and every minion, mount and fashion accessory the game declares, in
/// one searchable list. Cameras, lights and references stay absent (not
/// disabled) until their runtime entity types exist.</para>
/// </summary>
public sealed class SpawnBrowserWindow : Window
{
    // Fixed row order: the actions lead the list, so an empty query shows them
    // on top and a query that matches one keeps it above the catalog.
    private const int RowNewActor = 0;
    private const int RowNewActorCompanion = 1;
    private const int RowCloneActor = 2;
    private const int RowProp = 3;
    private const int ActionRows = 4;

    /// <summary>Double-click is a supported gesture on a single-click list, so
    /// a second activation of the SAME row inside this window is swallowed
    /// rather than spawning twice.</summary>
    private const double ReactivationSwallow = 0.35;

    private const string NoActorNote =
        "Minions, mounts and accessories attach to the selected actor — "
        + "select an actor first.";

    private const string NoSlotNote =
        "The selected actor has no companion slot — use "
        + "'New actor with companion slot'.";

    private static readonly string[] KindBadges = ["Minion", "Mount", "Accessory"];

    private readonly IActorSpawnService _spawnService;
    private readonly Game.PropSpawnService _propService;
    private readonly ISpawnCatalogService _catalog;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly ITextureProvider _textures;
    private readonly HashSet<uint> _missingIcons = new();
    private readonly SpawnBrowserViewModel _vm = new();

    private bool _built;
    private string _query = string.Empty;
    private string _queryLower = string.Empty;
    private bool _refilter = true;

    // The caption is a STRING PER COUNT, not per frame: it is rebuilt only when
    // the number it states or the mode it states it in changes.
    private string _caption = string.Empty;
    private int _captionCount = -1;
    private bool _captionFiltered;

    /// <summary>Why the last activation did nothing, or null. Cleared by the
    /// next activation and by any query change.</summary>
    private string? _note;

    private int _lastRow = -1;
    private double _lastActivatedAt;
    private IActor? _pendingSelectSpawned;

    public SpawnBrowserWindow(
        IActorSpawnService spawnService,
        Game.PropSpawnService propService,
        ISpawnCatalogService catalog,
        SelectionSession selection,
        StableBindingRegistry bindings,
        ITextureProvider textures)
        : base($"Add to scene###{PluginConstants.PluginName}_spawn_browser",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _spawnService = spawnService;
        _propService = propService;
        _catalog = catalog;
        _selection = selection;
        _bindings = bindings;
        _textures = textures;

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnActivate = Activate;
        _vm.OnClose = () => IsOpen = false;
        _vm.ResolveIcon = ResolveIcon;
    }

    public override void OnOpen()
    {
        BuildRows();
        // The query is a DRAFT: it means nothing outside the open surface, so
        // each open starts on the whole list.
        _vm.Query = string.Empty;
        _note = null;
        _lastRow = -1;
    }

    public override void PreDraw()
    {
        Size = new Vector2(
            SpawnBrowserView.DesignWidth, SpawnBrowserView.DesignHeight);
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        ReconcilePendingSpawn();
        SyncQuery();
        if (_refilter)
            Refilter();
        SyncCloneRow();
        SyncStatus();

        // The view paints its own chassis (frame + chrome); the host window is
        // an undecorated, transparent shell that only supplies position + input.
        var min = ImGui.GetWindowPos();
        var owner = Interactive.BeginOwner(
            "poser-spawn-browser",
            InteractionLayer.Window,
            min,
            min + ImGui.GetWindowSize());
        try
        {
            SpawnBrowserView.Draw(_vm, min);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    // ── the list ─────────────────────────────────────────────────────────

    /// <summary>Every row, minted once: the catalog is the sheets' whole
    /// admissible set and cannot change inside a session.</summary>
    private void BuildRows()
    {
        if (_built)
            return;
        _built = true;

        var rows = _vm.Rows;
        rows.Add(ActionRow(
            "##spawn-new-actor", "New actor", TablerIcon.UserPlus));
        rows.Add(ActionRow(
            "##spawn-new-actor-companion",
            "New actor with companion slot",
            TablerIcon.Paw));
        rows.Add(ActionRow(
            "##spawn-clone-actor", "Clone selected actor", TablerIcon.Stack2));
        rows.Add(ActionRow("##spawn-prop", "Prop", TablerIcon.Diamond));

        var entries = _catalog.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            rows.Add(new SpawnBrowserRow(
                "##spawn-catalog-" + i.ToString(CultureInfo.InvariantCulture),
                entry.Name,
                entry.NameLower,
                TablerIcon.Circle,
                entry.IconId,
                Badge(entry.Kind),
                false));
        }
        _refilter = true;
    }

    private static SpawnBrowserRow ActionRow(
        string id, string label, TablerIcon glyph) =>
        new(id, label, label.ToLowerInvariant(), glyph, 0u, null, false);

    private static string? Badge(CompanionKind kind) => kind switch
    {
        CompanionKind.Companion => KindBadges[0],
        CompanionKind.Mount => KindBadges[1],
        CompanionKind.Ornament => KindBadges[2],
        _ => null,
    };

    private void SyncQuery()
    {
        if (string.Equals(_query, _vm.Query, StringComparison.Ordinal))
            return;
        _query = _vm.Query;
        // Lowercased ONCE per query change; the scan below compares ordinal
        // against names that were lowercased when the catalog was built.
        _queryLower = _query.Trim().ToLowerInvariant();
        _note = null;
        _refilter = true;
    }

    /// <summary>The visible list, refilled in place. A keystroke runs THIS and
    /// nothing else; no cap, because the clipper makes the full list cheap.
    /// </summary>
    private void Refilter()
    {
        _refilter = false;
        var visible = _vm.Visible;
        var rows = _vm.Rows;
        visible.Clear();
        for (int i = 0; i < rows.Count; i++)
            if (_queryLower.Length == 0
                || rows[i].LabelLower.Contains(
                    _queryLower, StringComparison.Ordinal))
                visible.Add(i);
    }

    /// <summary>Clone is the one row whose availability moves with the
    /// selection, so it is the one row rewritten per frame.</summary>
    private void SyncCloneRow()
    {
        bool disabled = SelectedActor() is null;
        var row = _vm.Rows[RowCloneActor];
        if (row.Disabled != disabled)
            _vm.Rows[RowCloneActor] = row with { Disabled = disabled };
    }

    private void SyncStatus()
    {
        if (_note is { } note)
        {
            _vm.Status = note;
            return;
        }
        bool filtered = _queryLower.Length > 0;
        if (_captionCount != _vm.Visible.Count || _captionFiltered != filtered)
        {
            _captionCount = _vm.Visible.Count;
            _captionFiltered = filtered;
            _caption = _captionCount.ToString(CultureInfo.InvariantCulture)
                + (filtered ? " matches" : " spawnables");
        }
        _vm.Status = _caption;
    }

    // ── activation ───────────────────────────────────────────────────────

    private void Activate(int index)
    {
        double now = ImGui.GetTime();
        if (index == _lastRow && now - _lastActivatedAt < ReactivationSwallow)
            return;
        _lastRow = index;
        _lastActivatedAt = now;
        _note = null;

        switch (index)
        {
            case RowNewActor:
                SelectSpawned(
                    _spawnService.SpawnNewActor(reserveCompanionSlot: false));
                return;
            case RowNewActorCompanion:
                SelectSpawned(
                    _spawnService.SpawnNewActor(reserveCompanionSlot: true));
                return;
            case RowCloneActor:
                if (SelectedActor() is { } source)
                    SelectSpawned(_spawnService.CloneActor(source));
                return;
            case RowProp:
                _propService.SpawnProp();
                return;
        }

        // Catalog rows attach to the selected actor; they create nothing of
        // their own, so with no actor there is nothing to attach to.
        var entry = _catalog.Entries[index - ActionRows];
        if (SelectedActor() is not { } owner)
        {
            _note = NoActorNote;
            return;
        }
        if (!_spawnService.SetCompanion(
                owner, new CompanionAttachment(entry.Kind, entry.Id)))
            _note = NoSlotNote;
    }

    /// <summary>The selection's actor — a bone selection resolves to the actor
    /// that owns it — as a live actor, or null when nothing resolves.</summary>
    private IActor? SelectedActor()
    {
        var actorId = _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
            { Kind: SceneEntityKind.Bone, Bone: { } bone } =>
                bone.Skeleton.Actor,
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeActor } =>
                gazeActor,
            _ => (ActorId?)null,
        };
        if (actorId is not { } id)
            return null;
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    /// <summary>Selects a freshly spawned actor so the thing just created is
    /// the thing being edited. The scene has not rescanned yet, so the id is
    /// resolved on the next refresh rather than here.</summary>
    private void SelectSpawned(IActor? spawned)
    {
        if (spawned == null)
            return;
        _pendingSelectSpawned = spawned;
    }

    /// <summary>Second half of <see cref="SelectSpawned"/>: once the scene
    /// refresh has bound the new actor, select it and forget it.</summary>
    private void ReconcilePendingSpawn()
    {
        if (_pendingSelectSpawned is not { } spawned)
            return;
        if (_bindings.GetActorId(spawned) is not { } id)
            return;
        _selection.Select(SelectionId.ForActor(id));
        _pendingSelectSpawned = null;
    }

    /// <summary>
    /// Resolves a row's game icon to an ImGui handle, or 0 when there is none.
    /// Sheet icon ids are not guaranteed to exist and GetFromGameIcon THROWS for
    /// those, so this uses the try-variant, catches anyway, and remembers the
    /// failures. The WRAP is never cached: shared textures must be re-resolved
    /// each frame.
    /// </summary>
    private nint ResolveIcon(uint iconId)
    {
        if (iconId == 0 || _missingIcons.Contains(iconId))
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            if (_textures.TryGetFromGameIcon(
                    new GameIconLookup(iconId), out var shared))
                wrap = shared.GetWrapOrDefault();
            else
                _missingIcons.Add(iconId);
        }
        catch (Exception)
        {
            _missingIcons.Add(iconId);
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }
}
