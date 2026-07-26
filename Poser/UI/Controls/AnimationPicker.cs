using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.UI.Views;

namespace Poser.UI.Controls;

/// <summary>Where a picked animation is sent. The CALLER decides this;
/// the picker only reports the choice.</summary>
public enum AnimationPickTarget
{
    Base,
    Slot,
    Lips,
    Expression,
}

public readonly record struct AnimationPick(
    TimelineEntry Entry,
    AnimationPickTarget Target,
    AnimationSlot Slot,
    bool PlayImmediately);

/// <summary>
/// The ONE animation picker: an anchored glass popover with search, a kind
/// filter, and icon/name/id rows.
///
/// Every destination opens it — the base animation, a layer, the
/// expression, the lips — and the CALLER states the destination, so the
/// product has one search surface rather than one per control. It reports
/// the choice and nothing else.
///
/// Height shrinks to the results: a fixed tall popover over three matches
/// is mostly empty glass. Only the results list scrolls; the search field
/// and kind filter stay put, so what is being typed into never moves.
/// </summary>
public sealed class AnimationPicker
{
    private const string PopupId = "##anim-picker";
    private const float Width = 380f;
    private const float RowHeight = 26f;
    private const int MaxListRows = 12;
    private const int MinListRows = 3;

    private readonly AnimationCatalog _catalog;
    private readonly ITextureProvider _textures;
    private readonly HashSet<uint> _missingIcons = new();

    private string _search = string.Empty;
    private int _kindIndex;
    private bool _openRequested;
    private Vector2 _anchorMin;
    private Vector2 _anchorMax;
    private AnimationPickTarget _target;
    private AnimationSlot _slot;
    private AnimationSlot? _slotFilter;
    private string _caption = string.Empty;
    /// <summary>When set, the picker shows exactly these rows and hides the
    /// kind filter — used where the valid set is a known enumeration rather
    /// than a catalog query, such as the speech timelines.</summary>
    private IReadOnlyList<TimelineEntry>? _explicit;
    private bool _playOnSelect = true;

    private static readonly string[] KindLabels = { "All", "Emote", "Action", "Expr", "Raw" };
    private static readonly AnimationKind?[] KindValues =
    {
        null, AnimationKind.Emote, AnimationKind.Action,
        AnimationKind.Expression, AnimationKind.RawTimeline,
    };

    private static readonly string[] WeaponLabels = { "All", "Sheathed", "Drawn" };
    /// <summary>0 = all, 1 = sheathed, 2 = drawn. Brio's tri-filter: it
    /// narrows EMOTES by their weapon state and leaves actions and raw
    /// timelines alone, and only the Base destination shows it — a blended
    /// one-shot does not change weapon state. Persists across opens, like
    /// Brio's.</summary>
    private int _weaponFilter;

    public AnimationPicker(AnimationCatalog catalog, ITextureProvider textures)
    {
        _catalog = catalog;
        _textures = textures;
    }

    /// <summary>
    /// Requests the picker under the control that asked for it. The open is
    /// deferred to <see cref="Draw"/> because a popup opened from inside a
    /// scrolling child parents to that child and closes the same frame.
    /// </summary>
    public void Open(
        AnimationPickTarget target,
        AnimationSlot slot,
        AnimationSlot? restrictToSlot,
        string caption,
        AnimationKind? kind = null,
        IReadOnlyList<TimelineEntry>? entries = null)
    {
        _target = target;
        _slot = slot;
        _slotFilter = restrictToSlot;
        _caption = caption;
        _explicit = entries;
        _search = string.Empty;
        var start = kind ?? AnimationCatalog.BestKind(restrictToSlot);
        _kindIndex = Array.IndexOf(KindValues, start);
        if (_kindIndex < 0)
            _kindIndex = 0;
        _anchorMin = ImGui.GetItemRectMin();
        _anchorMax = ImGui.GetItemRectMax();
        _openRequested = true;
    }

    /// <summary>
    /// Draws the picker if open. Call once per frame from the pane's top
    /// level, outside any child. Returns the pick made this frame.
    /// </summary>
    public AnimationPick? Draw()
    {
        if (_openRequested)
        {
            ImGui.OpenPopup(PopupId);
            _openRequested = false;
        }
        if (!ImGui.IsPopupOpen(PopupId))
            return null;

        var results = Results(out var kinds, out var kindIndex);
        bool showWeapon = ShowWeaponFilter;
        AnimationPick? picked = null;
        Crystarium.Popover(PopupId, new PopoverProps
        {
            Width = Width,
            Height = HeightFor(results.Count, kinds.Count > 1, showWeapon),
            AnchorMin = _anchorMin,
            AnchorMax = _anchorMax,
        }, () => picked = DrawBody(results, kinds, kindIndex, showWeapon));
        return picked;
    }

    private bool ShowWeaponFilter =>
        _target == AnimationPickTarget.Base && _explicit == null;

    /// <summary>Chrome plus as many rows as there are, between a floor that
    /// keeps the empty state readable and a ceiling that keeps the popover
    /// on screen.</summary>
    private static float HeightFor(int resultCount, bool showKinds, bool showWeapon)
    {
        float chrome = 18f + 32f + (showKinds ? 34f : 0f) + (showWeapon ? 34f : 0f) + 30f + 16f;
        int rows = Math.Clamp(resultCount, MinListRows, MaxListRows);
        return chrome + rows * RowHeight;
    }

    private IReadOnlyList<TimelineEntry> Results(
        out List<AnimationKind?> kinds, out int kindIndex)
    {
        kinds = new List<AnimationKind?>();
        kindIndex = 0;
        if (_explicit is { } entries)
        {
            if (string.IsNullOrWhiteSpace(_search))
                return entries;
            var filtered = new List<TimelineEntry>();
            foreach (var entry in entries)
                if (entry.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.TimelineId.ToString() == _search.Trim())
                    filtered.Add(entry);
            return filtered;
        }

        // Kinds a restricted slot can never contain are dropped, so the
        // filter never offers a choice that returns nothing. "All" (null)
        // is never impossible and always leads.
        var excluded = AnimationCatalog.ExcludedKinds(_slotFilter);
        foreach (var value in KindValues)
        {
            bool blocked = false;
            if (value is { } concrete)
                foreach (var kind in excluded)
                    if (kind == concrete)
                        blocked = true;
            if (!blocked)
                kinds.Add(value);
        }

        var current = KindValues[Math.Clamp(_kindIndex, 0, KindValues.Length - 1)];
        kindIndex = kinds.IndexOf(current);
        if (kindIndex < 0)
            kindIndex = 0;
        var found = _catalog.Search(_search, kinds[kindIndex], _slotFilter, limit: 400);
        if (!ShowWeaponFilter || _weaponFilter == 0)
            return found;

        bool drawn = _weaponFilter == 2;
        var narrowed = new List<TimelineEntry>(found.Count);
        foreach (var entry in found)
            if (entry.DrawsWeapon is not { } state || state == drawn)
                narrowed.Add(entry);
        return narrowed;
    }

    private AnimationPick? DrawBody(
        IReadOnlyList<TimelineEntry> results, List<AnimationKind?> kinds, int kindIndex,
        bool showWeapon)
    {
        float s = ImGuiHelpers.GlobalScale;
        float inner = Width - 16f;
        // `scrollbar-gutter: stable` — the rows reserve the scrollbar's
        // 12px whether or not it is showing, so the list does not reflow
        // the instant it starts scrolling.
        float rowWidth = inner - Views.AppShellView.ScrollbarWidth;
        var origin = ImGui.GetCursorScreenPos();
        var cursor = origin;

        // The destination, stated where the choosing happens.
        ViewText.Label(cursor, _caption, 11f, FontWeight.Medium, InspectorLayout.LabelColor);
        cursor.Y += 18f * s;

        ImGui.SetCursorScreenPos(cursor);
        Crystarium.FilterPill("##anim-pick-search", ref _search, "Search name or id", inner);
        cursor.Y += 32f * s;

        if (kinds.Count > 1)
        {
            var labels = new string[kinds.Count];
            for (int i = 0; i < kinds.Count; i++)
                labels[i] = KindLabels[Array.IndexOf(KindValues, kinds[i])];
            ImGui.SetCursorScreenPos(cursor);
            int chosen = kindIndex;
            if (Crystarium.SegmentedControl("##anim-pick-kind", labels, ref chosen, inner))
                _kindIndex = Array.IndexOf(KindValues, kinds[chosen]);
            cursor.Y += 34f * s;
        }

        if (showWeapon)
        {
            ImGui.SetCursorScreenPos(cursor);
            int weapon = _weaponFilter;
            if (Crystarium.SegmentedControl("##anim-pick-weapon", WeaponLabels, ref weapon, inner))
                _weaponFilter = weapon;
            cursor.Y += 34f * s;
        }

        if (!_catalog.IsLoaded && _explicit == null)
        {
            ViewText.Label(cursor, "Building animation catalog…", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return null;
        }

        AnimationPick? picked = null;
        float footer = 30f * s;
        float listHeight = MathF.Max(
            RowHeight * MinListRows * s,
            (ImGui.GetWindowSize().Y - 16f * s) - (cursor.Y - origin.Y) - footer);

        ImGui.SetCursorScreenPos(cursor);
        Crystarium.PushScrollbarStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("##anim-pick-list", new Vector2(inner * s, listHeight),
                false, ImGuiWindowFlags.NoSavedSettings))
        {
            if (results.Count == 0)
            {
                ViewText.Label(ImGui.GetCursorScreenPos() + new Vector2(0f, 6f * s),
                    "No matches.", 11f, FontWeight.Regular, InspectorLayout.HintColor);
            }
            foreach (var entry in results)
            {
                if (Crystarium.SidebarRow(
                        $"##pick-{entry.TimelineId}-{(int)entry.Slot}",
                        entry.Name,
                        new SidebarRowProps
                        {
                            Icon = FallbackIcon(entry.Kind),
                            IconTexture = ResolveIcon(entry.Icon),
                            Badge = Metadata(entry),
                            NoExpanderSlot = true,
                            Width = rowWidth,
                        }))
                {
                    picked = new AnimationPick(entry, _target, _slot, _playOnSelect);
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        Crystarium.PopScrollbarStyle();
        cursor.Y += listHeight;

        // Footer: the one option that belongs to the act of picking.
        ImGui.SetCursorScreenPos(cursor + new Vector2(0f, 6f * s));
        Crystarium.Switch("##anim-pick-play", ref _playOnSelect);
        ViewText.Label(cursor + new Vector2(46f * s, 8f * s), "Play when selected", 11f,
            FontWeight.Regular, InspectorLayout.LabelColor);

        if (picked != null)
            ImGui.CloseCurrentPopup();
        return picked;
    }

    /// <summary>The badge carries what matters for the destination: the id
    /// always, and the slot too when the picker is not already restricted
    /// to one — that is the difference between a body and a face timeline.</summary>
    private string Metadata(TimelineEntry entry) =>
        _slotFilter != null
            ? entry.TimelineId.ToString()
            : $"{AnimationSlots.DisplayName(entry.Slot)} · {entry.TimelineId}";

    /// <summary>Glyph for rows the game gives no icon for — every raw
    /// timeline. Keyed by kind so the column still reads at a glance.</summary>
    private static TablerIcon FallbackIcon(AnimationKind kind) => kind switch
    {
        AnimationKind.Emote or AnimationKind.Expression => TablerIcon.MoodSmile,
        AnimationKind.Action => TablerIcon.Bolt,
        _ => TablerIcon.Movie,
    };

    /// <summary>
    /// Resolves a row's game icon, or null when there is none. Sheet icon
    /// ids are not guaranteed to exist and GetFromGameIcon THROWS for
    /// those, so this uses the try-variant, catches anyway, and remembers
    /// the failures — an exception per row per frame is a frame-rate
    /// cliff. The WRAP is never cached: shared textures must be
    /// re-resolved each frame.
    /// </summary>
    private IDalamudTextureWrap? ResolveIcon(uint iconId)
    {
        if (iconId == 0 || _missingIcons.Contains(iconId))
            return null;
        try
        {
            if (_textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var shared))
                return shared.GetWrapOrDefault();
            _missingIcons.Add(iconId);
        }
        catch (Exception)
        {
            _missingIcons.Add(iconId);
        }
        return null;
    }
}
