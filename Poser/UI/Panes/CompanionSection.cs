using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.Application.Companions;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using Attachment = Poser.Game.Types.CompanionAttachment;
using AttachmentKind = Poser.Game.Types.CompanionKind;

namespace Poser.UI;

/// <summary>
/// The companion-slot attach surface: a <see cref="Crystarium.SearchPicker{T}"/>
/// over the whole minion/mount/ornament catalog, opened from an actor's
/// context menu and applied to that actor's native slot. Deliberately NOT a
/// pane section — standalone creatures come from the spawn browser, and the
/// slot only matters for riding a mount or carrying an ornament, so the
/// surface stays out of the way until asked for.
///
/// <para>The catalog's kind and the native container's kind are DIFFERENT
/// enums — a catalog row is always attachable, so it has no None — and they
/// are mapped explicitly here, at the only place both are in scope.</para>
/// </summary>
public sealed class CompanionSection
{
    private readonly CompanionCatalog _catalog;
    private readonly IActorSpawnService _spawn;
    private readonly StableBindingRegistry _bindings;
    private readonly ITextureProvider _textures;

    private readonly Crystarium.SearchPicker<CompanionEntry> _picker =
        new("companion");

    /// <summary>The OWNER actor frozen when the surface opened — a minion row
    /// and its owner's row both attach through the owner, and a selection
    /// change while the popover is up never retargets the pending pick.
    /// </summary>
    private ActorId? _pickOwner;

    /// <summary>The strip's selection, controlled and persistent like every
    /// other picker strip; an open with something attached reseeds it because
    /// a swap starts from what the actor already is.</summary>
    private int _kindIndex;

    /// <summary>Sheet icon ids are not guaranteed to exist and the game icon
    /// lookup THROWS for those, so a failure is remembered: an exception per
    /// row per frame is a frame-rate cliff.</summary>
    private readonly HashSet<uint> _missingIcons = new();

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
        IActorSpawnService spawn,
        StableBindingRegistry bindings,
        ITextureProvider textures)
    {
        _catalog = catalog;
        _spawn = spawn;
        _bindings = bindings;
        _textures = textures;
        _query = Compute;
        _entryKey = RowKey;
        _entryTexture = entry => ResolveIcon(entry.Icon);
        _entryBadge = Badge;
        _setKind = chosen => _kindIndex = chosen;
    }

    // ── the one surface ──────────────────────────────────────────────────

    /// <summary>Opens the picker against the owner frozen here, seeded to what
    /// the slot already carries so an attach reads as a swap when one is
    /// there.</summary>
    public void OpenAttachPicker(ActorId ownerId)
    {
        if (Resolve(ownerId) is not { } owner)
            return;
        _pickOwner = ownerId;
        var current = _spawn.GetCompanionInfo(owner);
        if (ToCatalog(current.Kind) is { } seed)
        {
            int index = Array.IndexOf(KindValues, (CompanionKind?)seed);
            _kindIndex = index < 0 ? 0 : index;
        }
        _picker.Open(
            "companion",
            Array.Empty<CompanionEntry>(),
            EntryName,
            _entryKey,
            Known(current) is { } entry ? RowKey(entry) : null,
            _catalog.IsLoaded ? null : "Building minion catalog…",
            Options());
    }

    /// <summary>The strip is CONTROLLED — its selection lives here — so the
    /// open surface is re-told its options each frame before it draws. Pumped
    /// by the pane at window level so it survives a section collapse.</summary>
    public void DrawPicker()
    {
        _picker.Update(Options());
        if (_picker.Draw() is not { } chosen || _pickOwner is not { } ownerId)
            return;
        if (Resolve(ownerId) is not { } owner)
            return;
        // One call both attaches and swaps: the backend empties the slot
        // before it fills it. A failure is the service's log line — the menu
        // item that opens this surface is gated on the slot existing.
        _spawn.SetCompanion(
            owner,
            new Attachment(ToAttachment(chosen.Item.Kind), chosen.Item.Id));
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

    // ── kind mapping ─────────────────────────────────────────────────────

    /// <summary>Catalog kind to container kind. Total: every catalog row is
    /// attachable, which is what having no None means.</summary>
    private static AttachmentKind ToAttachment(CompanionKind kind) => kind switch
    {
        CompanionKind.Mount => AttachmentKind.Mount,
        CompanionKind.Ornament => AttachmentKind.Ornament,
        _ => AttachmentKind.Companion,
    };

    /// <summary>Container kind to catalog kind, null for the empty slot —
    /// nothing attached has no catalog row.</summary>
    private static CompanionKind? ToCatalog(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Companion => CompanionKind.Companion,
        AttachmentKind.Mount => CompanionKind.Mount,
        AttachmentKind.Ornament => CompanionKind.Ornament,
        _ => null,
    };

    // ── helpers ──────────────────────────────────────────────────────────

    private IActor? Resolve(ActorId id)
    {
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    private CompanionEntry? Known(Attachment current) =>
        ToCatalog(current.Kind) is { } kind
            ? _catalog.Find(kind, current.Id)
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

    /// <summary>
    /// Resolves a row's game icon to an ImGui handle, or 0 when there is none.
    /// Sheet icon ids are not guaranteed to exist and GetFromGameIcon THROWS
    /// for those, so this uses the try-variant, catches anyway, and remembers
    /// the failures. The WRAP is never cached: shared textures must be
    /// re-resolved each frame.
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
