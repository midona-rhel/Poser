using System;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Plugin.Services;
using Poser.Application.Companions;
using Poser.Application.Scene;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// The companion-slot attach surface: a <see cref="Crystarium.SearchPicker{T}"/>
/// over the whole minion/mount/ornament catalog, opened from an actor's
/// context menu or Actor &gt; General and applied to that actor's native slot.
/// Standalone creatures still come from the spawn browser; this control owns
/// only an actor's native minion, mount, or ornament slot.
/// </summary>
public sealed class CompanionSection
{
    private readonly CompanionCatalog _catalog;
    private readonly ICompanionCatalogLoader _catalogLoader;
    private readonly IActorSpawnService _spawn;
    private readonly IEntityBindings _bindings;
    private readonly SceneSession _scene;
    private readonly Game.Journal.ActorValueSession _values;
    private readonly UserNotices _notices;

    private readonly Crystarium.SearchPicker<CompanionEntry> _picker =
        new("companion");

    /// <summary>The OWNER actor frozen when the surface opened — a minion row
    /// and its owner's row both attach through the owner, and a selection
    /// change while the popover is up never retargets the pending pick.
    /// </summary>
    private ActorId? _pickOwner;
    private ActorId? _pickSubject;

    /// <summary>The strip's selection, controlled and persistent like every
    /// other picker strip; an open with something attached reseeds it because
    /// a swap starts from what the actor already is.</summary>
    private int _kindIndex;

    private readonly GameIconResolver _icons;

    private readonly Dictionary<ushort, string> _idText = new();
    private readonly Dictionary<int, string> _rowKeys = new();

    // The memo and the exact inputs its answer was computed from: the open
    // surface asks for the visible list every frame.
    private string? _memoQuery;
    private int _memoKind = -1;
    private bool _memoLoaded;
    private IReadOnlyList<CompanionEntry> _memo = Array.Empty<CompanionEntry>();

    private static readonly string[] KindLabels =
        ["All", "Minions", "Mounts", "Ornaments"];

    private static readonly CompanionKind?[] KindValues =
    [
        null, CompanionKind.Companion, CompanionKind.Mount,
        CompanionKind.Ornament,
    ];

    private static readonly Func<CompanionEntry, string> EntryName =
        static entry => entry.Name;

    private readonly Func<string, IReadOnlyList<CompanionEntry>> _query;
    private readonly Func<CompanionEntry, string> _entryKey;
    private readonly Func<CompanionEntry, nint> _entryTexture;
    private readonly Func<CompanionEntry, string?> _entryBadge;
    private readonly Action<int> _setKind;

    public CompanionSection(
        CompanionCatalog catalog,
        ICompanionCatalogLoader catalogLoader,
        IActorSpawnService spawn,
        IEntityBindings bindings,
        SceneSession scene,
        ITextureProvider textures,
        Game.Journal.ActorValueSession values,
        UserNotices notices)
    {
        _values = values;
        _catalog = catalog;
        _catalogLoader = catalogLoader;
        _spawn = spawn;
        _bindings = bindings;
        _scene = scene;
        _notices = notices;
        _icons = new GameIconResolver(textures);
        _query = Compute;
        _entryKey = RowKey;
        _entryTexture = entry => _icons.Resolve(entry.Icon);
        _entryBadge = Badge;
        _setKind = chosen => _kindIndex = chosen;
    }

    // ── the one surface ──────────────────────────────────────────────────

    /// <summary>The exact owner-routed verbs available to an actor row.</summary>
    public readonly record struct ActionState(bool IsAttachedChild, bool Occupied);

    /// <summary>Describes the current exact relationship. Attached bodies route
    /// through their owner; root actors are admitted only when they own a slot.</summary>
    public ActionState? ActionsFor(ActorId subjectId)
    {
        if (!TryResolveOwner(subjectId, out _, out var owner, out bool child))
            return null;
        return new ActionState(child, _spawn.GetCompanionInfo(owner) is not null);
    }

    /// <summary>Opens the picker against the exact owner resolved from either
    /// the owner row or its attached child. A stale child cannot retarget a new
    /// relationship because the descriptor and binding must both still match.</summary>
    public bool OpenAttachPicker(ActorId subjectId)
    {
        if (!TryResolveOwner(subjectId, out var ownerId, out var owner, out _))
            return false;
        _catalogLoader.EnsureLoaded();
        _pickSubject = subjectId;
        _pickOwner = ownerId;
        var current = _spawn.GetCompanionInfo(owner);
        if (current is { } attached)
        {
            int index = Array.IndexOf(KindValues, (CompanionKind?)attached.Kind);
            _kindIndex = index < 0 ? 0 : index;
        }
        _picker.Open(
            "companion",
            Array.Empty<CompanionEntry>(),
            EntryName,
            _entryKey,
            Known(current) is { } entry ? RowKey(entry) : null,
            _catalog.IsLoaded ? null : "Building minion catalog…",
            Options(),
            // Opened from a context menu: the click IS the initiating
            // control, so the surface spawns where the user clicked.
            anchor: ImGui.GetMousePos());
        return true;
    }

    /// <summary>Detaches through the current exact owner relationship.</summary>
    public bool Detach(ActorId subjectId)
    {
        if (!TryResolveOwner(subjectId, out _, out var owner, out _)
            || _spawn.GetCompanionInfo(owner) is null)
            return false;
        return _values.SetCompanion(owner, null);
    }

    /// <summary>The strip is CONTROLLED — its selection lives here — so the
    /// open surface is re-told its options each frame before it draws. Pumped
    /// by the pane at window level so it survives a section collapse.</summary>
    public void DrawPicker()
    {
        // The catalog builds in the background: the status is RE-TOLD every
        // frame, or a picker opened mid-build would show "Building" forever
        // (the notice was an open-time argument and nothing refreshed it).
        _picker.SetLoadStatus(
            _catalog.IsLoaded ? null : "Building minion catalog…");
        _picker.Update(Options());
        if (_picker.Draw() is not { } chosen
            || _pickSubject is not { } subjectId
            || _pickOwner is not { } frozenOwner)
            return;
        if (!TryResolveOwner(
                subjectId, out var ownerId, out var owner, out _)
            || ownerId != frozenOwner)
            return;
        // One call both attaches and swaps: the backend empties the slot
        // before it fills it. The menu item that opens this surface is gated
        // on the slot existing, but the native write can still be refused.
        if (!_values.SetCompanion(
                owner,
                new CompanionAttachment(chosen.Item.Kind, chosen.Item.Id)))
            _notices.Refused(
                "Attachment",
                "The game refused the companion-slot change.");
    }

    private PickerOptions<CompanionEntry> Options() => new()
    {
        Query = _query,
        Texture = _entryTexture,
        Glyph = static _ => TablerIcon.Paw,
        Badge = _entryBadge,
        Strip = new PickerStrip(KindLabels, _kindIndex, _setKind),
        // A row carries an icon, a name and a badge, and the narrow picker
        // cuts all three.
        Width = Crystarium.ActiveTheme.Picker.WideWidth,
    };

    private IReadOnlyList<CompanionEntry> Compute(string search)
    {
        bool loaded = _catalog.IsLoaded;
        if (_memoQuery == search && _memoKind == _kindIndex
            && _memoLoaded == loaded)
            return _memo;
        _memoQuery = search;
        _memoKind = _kindIndex;
        _memoLoaded = loaded;
        _memo = _catalog.Search(
            search,
            KindValues[Math.Clamp(_kindIndex, 0, KindValues.Length - 1)],
            limit: 400);
        return _memo;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private bool TryResolveOwner(
        ActorId subjectId,
        out ActorId ownerId,
        out IActor owner,
        out bool attachedChild)
    {
        ownerId = default;
        owner = null!;
        attachedChild = false;
        CompanionKind? childKind = null;
        if (_scene.Snapshot.FindActor(subjectId) is not { } subject)
            return false;

        if (subject.OwnerActor is { } linkedOwner)
        {
            if (subject.AttachmentKind is not { } linkedKind
                || Resolve(subjectId) is null)
                return false;
            ownerId = linkedOwner;
            attachedChild = true;
            childKind = linkedKind;
        }
        else
        {
            ownerId = subject.Id;
        }

        if (Resolve(ownerId) is not { } exactOwner
            || !_spawn.HasCompanionSlot(exactOwner))
            return false;
        if (attachedChild)
        {
            var current = _spawn.GetCompanionInfo(exactOwner);
            if (current is null || current.Value.Kind != childKind)
                return false;
        }
        owner = exactOwner;
        return true;
    }

    private IActor? Resolve(ActorId id)
    {
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    /// <summary>The catalog row the slot currently carries, if any — an
    /// empty slot has no row.</summary>
    private CompanionEntry? Known(CompanionAttachment? current) =>
        current is { } attached
            ? _catalog.Find(attached.Kind, attached.Id)
            : null;

    /// <summary>A minion row is named by its id and the others by what they
    /// are — the kinds a caller has to tell apart at a glance.</summary>
    private string? Badge(CompanionEntry entry) =>
        entry.Kind == CompanionKind.Companion
            ? IdText(entry.Id)
            : entry.Kind == CompanionKind.Mount ? "Mount" : "Ornament";

    private string IdText(ushort id)
    {
        if (_idText.TryGetValue(id, out var text))
            return text;
        text = id.ToString(CultureInfo.InvariantCulture);
        _idText[id] = text;
        return text;
    }

    /// <summary>Ids are only unique WITHIN a sheet, so a row's identity is the
    /// kind and the id — two rows sharing an ImGui id share one another's
    /// hover and press.</summary>
    private string RowKey(CompanionEntry entry)
    {
        int identity = ((int)entry.Kind << 16) | entry.Id;
        if (_rowKeys.TryGetValue(identity, out var text))
            return text;
        text = identity.ToString(CultureInfo.InvariantCulture);
        _rowKeys[identity] = text;
        return text;
    }
}
