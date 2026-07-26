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
using Poser.Domain.Identity;
using Poser.UI.Views;

namespace Poser.UI.Controls;

/// <summary>Where a picked animation is sent. The CALLER decides this;
/// the picker only reports the choice.</summary>
public enum AnimationPickTarget
{
    Base,
    Blend,
    Slot,
    Lips,
}

public readonly record struct AnimationPick(
    TimelineEntry Entry,
    AnimationPickTarget Target,
    AnimationSlot Slot);

/// <summary>
/// The ONE animation picker: a glass popover with the shared filter pill,
/// segmented kind filter, and icon/name/id rows.
///
/// It is opened by Base, Blend, each slot's Select action, and Lips — the
/// caller supplies the destination, so there is exactly one search surface
/// in the product instead of one per control. The Animation page itself
/// stays compact sections and controls with no list of its own.
///
/// Search text is deliberately picker-local and transient: it belongs to
/// the act of picking, not to the actor, and clearing it between openings
/// is what makes the picker feel like a fresh search each time.
/// </summary>
public sealed class AnimationPicker
{
    private const string PopupId = "##anim-picker";
    private const float Width = 380f;
    private const float Height = 420f;

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
    /// <summary>Restricts results when the destination only accepts one
    /// slot, so a pick can never land a body timeline in the face.</summary>
    private AnimationSlot? _slotFilter;
    private string _caption = string.Empty;

    private static readonly string[] KindLabels = { "Emote", "Action", "Expr", "Raw" };
    private static readonly AnimationKind[] KindValues =
    {
        AnimationKind.Emote, AnimationKind.Action,
        AnimationKind.Expression, AnimationKind.RawTimeline,
    };

    public AnimationPicker(AnimationCatalog catalog, ITextureProvider textures)
    {
        _catalog = catalog;
        _textures = textures;
    }

    public bool IsOpenFor(AnimationPickTarget target, AnimationSlot slot) =>
        ImGui.IsPopupOpen(PopupId) && _target == target &&
        (target != AnimationPickTarget.Slot || _slot == slot);

    /// <summary>
    /// Requests the picker under the control that asked for it. The open
    /// is deferred to <see cref="Draw"/> because a popup opened from
    /// inside a scrolling child parents to that child and closes on the
    /// same frame.
    /// </summary>
    public void Open(
        AnimationPickTarget target,
        AnimationSlot slot,
        AnimationSlot? restrictToSlot,
        string caption)
    {
        _target = target;
        _slot = slot;
        _slotFilter = restrictToSlot;
        _caption = caption;
        _search = string.Empty;
        _kindIndex = Array.IndexOf(KindValues, AnimationCatalog.BestKind(restrictToSlot));
        if (_kindIndex < 0)
            _kindIndex = 0;
        _anchorMin = ImGui.GetItemRectMin();
        _anchorMax = ImGui.GetItemRectMax();
        _openRequested = true;
    }

    /// <summary>
    /// Draws the picker if open. Call once per frame from the pane's
    /// top level, outside any child. Returns the pick made this frame.
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

        AnimationPick? picked = null;
        Crystarium.Popover(PopupId, new PopoverProps
        {
            Width = Width,
            Height = Height,
            AnchorMin = _anchorMin,
            AnchorMax = _anchorMax,
        }, () => picked = DrawBody());
        return picked;
    }

    private AnimationPick? DrawBody()
    {
        float s = ImGuiHelpers.GlobalScale;
        float inner = Width - 16f; // popover padding both sides
        var origin = ImGui.GetCursorScreenPos();
        var cursor = origin;

        // Destination, stated in the picker so the user knows where a
        // click lands without remembering which control they used.
        ViewText.Label(cursor, _caption, 11f, FontWeight.Medium,
            InspectorLayout.LabelColor);
        cursor.Y += 18f * s;

        ImGui.SetCursorScreenPos(cursor);
        Crystarium.FilterPill("##anim-pick-search", ref _search, "Search name or id", inner);
        cursor.Y += 32f * s;

        // Kinds a restricted slot can never contain are dropped, so the
        // filter never offers a choice that returns nothing.
        var excluded = AnimationCatalog.ExcludedKinds(_slotFilter);
        var labels = new List<string>();
        var values = new List<AnimationKind>();
        for (int i = 0; i < KindValues.Length; i++)
        {
            bool blocked = false;
            foreach (var kind in excluded)
                if (kind == KindValues[i])
                    blocked = true;
            if (blocked)
                continue;
            labels.Add(KindLabels[i]);
            values.Add(KindValues[i]);
        }
        if (values.Count == 0)
        {
            labels.Add(KindLabels[^1]);
            values.Add(KindValues[^1]);
        }
        int selectedKind = values.IndexOf(
            KindValues[Math.Clamp(_kindIndex, 0, KindValues.Length - 1)]);
        if (selectedKind < 0)
            selectedKind = 0;
        ImGui.SetCursorScreenPos(cursor);
        if (Crystarium.SegmentedControl("##anim-pick-kind", labels.ToArray(),
                ref selectedKind, inner))
            _kindIndex = Array.IndexOf(KindValues, values[selectedKind]);
        cursor.Y += 34f * s;

        if (!_catalog.IsLoaded)
        {
            ViewText.Label(cursor, "Building animation catalog…", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return null;
        }

        var results = _catalog.Search(
            _search, values[selectedKind], _slotFilter, limit: 400);

        AnimationPick? picked = null;
        float listHeight = MathF.Max(
            60f * s, (Height - 16f) * s - (cursor.Y - origin.Y));
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
                            Badge = entry.TimelineId.ToString(),
                            NoExpanderSlot = true,
                        }))
                {
                    picked = new AnimationPick(entry, _target, _slot);
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        Crystarium.PopScrollbarStyle();

        if (picked != null)
            ImGui.CloseCurrentPopup();
        return picked;
    }

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
