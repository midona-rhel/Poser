using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

/// <summary>
/// The shell sidebar's search field, section headers and tree, drawn
/// imperatively over a FLAT cache of visible entries.
///
/// <para>The cache is rebuilt only when the scene revision, the filter text or
/// an expansion changes; a warm frame walks no section/row tree, builds no
/// string and submits only the band the clipper reports. Everything that can
/// change without a structural change — selection, badges, actor visibility,
/// the paused state — is read from the LIVE view-model row through the cached
/// (section, row) path, so the cache never has to hold a stale snapshot.</para>
///
/// <para>Expansion is NOT stored here. Rows carry Expanded/ExpandKey from the
/// view-model builder; a disclosure click flows out through
/// <see cref="AppShellViewModel.OnRowExpandToggled"/> and only marks the cache
/// dirty, so the next frame re-splices from the rebuilt rows.</para>
/// </summary>
public sealed class ShellSidebar
{
    /// <summary>The band above the tree — the search field's row.</summary>
    private const float SearchBandHeight = 38f;

    /// <summary>The search field's own top inset inside that band.</summary>
    private const float SearchTop = 6f;

    /// <summary>TreeRow's action-strip gap: the seats it reports are spaced by
    /// it, and the caller has to walk the strip with the same step.</summary>
    private const float ActionGap = 2f;

    private const string SearchId = "##shell-sidebar-search";
    private const string TreeId = "##shell-sidebar-tree";

    private enum EntryKind : byte { Header, Row }

    /// <summary>
    /// One visible entry, flattened. Everything here is DERIVED — the id
    /// string, the lower-cased label the filter reads, the guide mask the
    /// view-model states as a bool array, the vertical offset — so no frame
    /// has to compute it again.
    /// </summary>
    private readonly struct Entry
    {
        internal Entry(
            EntryKind kind,
            int section,
            int row,
            string id,
            string labelLower,
            int depth,
            uint trunks,
            int actions,
            float top,
            float height)
        {
            Kind = kind;
            Section = section;
            Row = row;
            Id = id;
            LabelLower = labelLower;
            Depth = depth;
            Trunks = trunks;
            Actions = actions;
            Top = top;
            Height = height;
        }

        internal readonly EntryKind Kind;
        internal readonly int Section;
        /// <summary>Index into the section's row list; -1 for a header.
        /// </summary>
        internal readonly int Row;
        internal readonly string Id;
        internal readonly string LabelLower;
        internal readonly int Depth;
        internal readonly uint Trunks;
        /// <summary>Square action slots the row reserves.</summary>
        internal readonly int Actions;
        /// <summary>Logical offset from the tree's top.</summary>
        internal readonly float Top;
        internal readonly float Height;

        internal float Bottom => Top + Height;

        internal Entry At(float top) => new(
            Kind, Section, Row, Id, LabelLower, Depth, Trunks, Actions,
            top, Height);
    }

    /// <summary>Every entry the view model states, flattened once per
    /// structural change. The filter reads THIS — never the view model.
    /// </summary>
    private readonly List<Entry> _source = new();

    /// <summary>The entries the filter kept, at their vertical offsets.
    /// </summary>
    private readonly List<Entry> _entries = new();

    /// <summary>Slot (one TreeRow pitch) to the first entry that reaches into
    /// it: the clipper reports slots, and this turns its band into the entry
    /// range without a search.</summary>
    private readonly List<int> _slots = new();

    /// <summary>Per-section row counts at the last rebuild. The view model is
    /// rebuilt every frame, so a structural change that does NOT bump the
    /// revision would otherwise leave the cached paths pointing past the end.
    /// </summary>
    private readonly List<int> _rowCounts = new();

    /// <summary>Filter scratch, parallel to <see cref="_source"/>: what the
    /// pass keeps, and the depth it kept it at.</summary>
    private bool[] _kept = new bool[64];
    private int[] _keptDepth = new int[64];

    private readonly Action<Crystarium.ScrollRegionScope> _drawTree;
    private readonly Action<string> _setSearch;

    /// <summary>The frame's view model. Written by <see cref="Draw"/> before
    /// anything can read it, so the hoisted callbacks always dispatch against
    /// the model the frame was handed.</summary>
    private AppShellViewModel _vm = null!;

    /// <summary>The ONE rebuild flag: the scene revision, a structural change,
    /// the row pitch, or a disclosure click. The filter is the one input that
    /// does not reach it — a keystroke refilters the cache in place.</summary>
    private bool _dirty = true;
    private bool _refilter = true;
    private ulong _revision;
    private string _filter = string.Empty;
    private string _filterLower = string.Empty;
    private float _pitch;
    private float _totalHeight;
    private int _slotCount;

    public ShellSidebar()
    {
        // Hoisted once: a per-frame lambda is exactly the cost this sidebar
        // exists to remove.
        _drawTree = DrawTree;
        _setSearch = next => _vm.SidebarSearch = next;
    }

    /// <summary>
    /// Draws the whole sidebar body into <paramref name="origin"/>/
    /// <paramref name="size"/> (screen space, already scaled): the search band
    /// on top, the tree in its own scroll region beneath it. The box is the
    /// sidebar's CONTENT box — the chassis, the divider rule and the status bar
    /// belong to the shell that seats this.
    /// </summary>
    public void Draw(AppShellViewModel vm, Vector2 origin, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset;
        float width = size.X / scale;

        // The pill spans the cell between the content inset and the 1px rule.
        float pillWidth = MathF.Max(1f, width - inset * 2f - 1f);
        ImGui.SetCursorScreenPos(origin + new Vector2(inset, SearchTop) * scale);
        Crystarium.FilterPill(
            SearchId,
            vm.SidebarSearch,
            _setSearch,
            "Filter scene...",
            ControlStyle.Workspace with { Width = UiWidth.Fixed(pillWidth) });

        Sync(vm, theme);

        // The search field stays OUTSIDE the scroll child so a large skeleton
        // cannot push the sidebar's primary navigation affordance out of view.
        float treeHeight = MathF.Max(1f, size.Y / scale - SearchBandHeight);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, SearchBandHeight * scale));
        Crystarium.ScrollRegion(TreeId, width, treeHeight, _drawTree);
    }

    // ── the cache ────────────────────────────────────────────────────────

    private void Sync(AppShellViewModel vm, Theme theme)
    {
        if (_revision != vm.SceneRevision)
        {
            _revision = vm.SceneRevision;
            _dirty = true;
        }

        if (!string.Equals(_filter, vm.SidebarSearch, StringComparison.Ordinal))
        {
            _filter = vm.SidebarSearch;
            _filterLower = _filter.Trim().ToLowerInvariant();
            _refilter = true;
        }

        // The row pitch is the cache's grid; a theme swap moves it.
        if (_pitch != theme.Controls.ListRowHeight)
        {
            _pitch = theme.Controls.ListRowHeight;
            _dirty = true;
        }

        if (!_dirty && Restructured(vm))
            _dirty = true;
        if (_dirty)
            Rebuild(vm, theme);
        if (_refilter)
            Splice(theme);
    }

    private bool Restructured(AppShellViewModel vm)
    {
        if (_rowCounts.Count != vm.Sections.Count)
            return true;
        for (int s = 0; s < _rowCounts.Count; s++)
            if (_rowCounts[s] != vm.Sections[s].Rows.Count)
                return true;
        return false;
    }

    /// <summary>Flattens the view model ONCE: every string the tree needs is
    /// minted here and nowhere else.</summary>
    private void Rebuild(AppShellViewModel vm, Theme theme)
    {
        _dirty = false;
        _refilter = true;
        _source.Clear();
        _rowCounts.Clear();
        float headerHeight = theme.Floating.CloseActionSize;
        float rowHeight = theme.Controls.ListRowHeight;

        for (int s = 0; s < vm.Sections.Count; s++)
        {
            var section = vm.Sections[s];
            _rowCounts.Add(section.Rows.Count);
            _source.Add(new Entry(
                EntryKind.Header, s, -1, HeaderId(s), string.Empty,
                0, 0u, 0, 0f, headerHeight));
            for (int r = 0; r < section.Rows.Count; r++)
            {
                var row = section.Rows[r];
                _source.Add(new Entry(
                    EntryKind.Row,
                    s,
                    r,
                    RowId(row),
                    row.Label.ToLowerInvariant(),
                    row.Depth,
                    Trunks(row.TreeLines),
                    row.ActorActions ? 3 : row.OverlayBones != null ? 1 : 0,
                    0f,
                    rowHeight));
            }
        }
    }

    /// <summary>
    /// The visible list: the filter pass over the cache's own lower-cased
    /// labels, the vertical offsets, and the clipper's slot index. A keystroke
    /// runs THIS and nothing else.
    ///
    /// <para>A row survives when it matches, when a descendant matches (it is
    /// the branch that reaches one), or when an ancestor matches (a matched
    /// group keeps its contents) — exactly the rows the view-model builder's
    /// own filter keeps, so running both is idempotent.</para>
    /// </summary>
    private void Splice(Theme theme)
    {
        _refilter = false;
        Keep();
        _entries.Clear();
        float y = 0f;
        int section = -1;
        for (int i = 0; i < _source.Count; i++)
        {
            if (!_kept[i])
                continue;
            var entry = _source[i];
            if (entry.Kind == EntryKind.Header && section >= 0)
                y += theme.Spacing.Four;
            section = entry.Section;
            _entries.Add(entry.At(y));
            y += entry.Height;
        }

        _totalHeight = y;
        // The clipper's grid is the ROW pitch, and the tail band is folded into
        // the last slot — so the seek its End() performs can never overshoot
        // the real content height.
        _slotCount = Math.Max(1, (int)(_totalHeight / _pitch));
        _slots.Clear();
        int at = 0;
        for (int slot = 0; slot < _slotCount; slot++)
        {
            float top = slot * _pitch;
            while (at + 1 < _entries.Count && _entries[at].Bottom <= top)
                at++;
            _slots.Add(at);
        }
    }

    /// <summary>Marks the source entries the filter keeps. Headers always
    /// survive: a section states what the sidebar contains, filtered or
    /// not.</summary>
    private void Keep()
    {
        int count = _source.Count;
        if (_kept.Length < count)
        {
            _kept = new bool[Math.Max(count, _kept.Length * 2)];
            _keptDepth = new int[_kept.Length];
        }

        if (_filterLower.Length == 0)
        {
            for (int i = 0; i < count; i++)
                _kept[i] = true;
            return;
        }

        // Matches, and every row under a matched row. A header resets the
        // walk: sections do not nest.
        int inside = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var entry = _source[i];
            if (entry.Kind == EntryKind.Header)
            {
                inside = int.MaxValue;
                _keptDepth[i] = -1;
                _kept[i] = true;
                continue;
            }

            _keptDepth[i] = entry.Depth;
            if (entry.Depth <= inside)
                inside = int.MaxValue;
            bool match = entry.LabelLower.Contains(
                _filterLower, StringComparison.Ordinal);
            if (match && entry.Depth < inside)
                inside = entry.Depth;
            _kept[i] = match || entry.Depth > inside;
        }

        // Ancestors: walking back, the nearest shallower row is the parent.
        int wanted = -1;
        for (int i = count - 1; i >= 0; i--)
        {
            if (_kept[i])
                wanted = _keptDepth[i] - 1;
            else if (_keptDepth[i] <= wanted)
            {
                _kept[i] = true;
                wanted = _keptDepth[i] - 1;
            }
        }
    }

    /// <summary>The view model's per-ancestor sibling flags as the painter's
    /// trunk mask, verbatim: bit <c>a</c> is <c>TreeLines[a]</c>, and bit 0 is
    /// unused exactly as depth 0 has no trunk.</summary>
    private static uint Trunks(bool[]? lines)
    {
        if (lines == null)
            return 0u;
        uint mask = 0u;
        int levels = Math.Min(lines.Length, 32);
        for (int level = 1; level < levels; level++)
            if (lines[level])
                mask |= 1u << level;
        return mask;
    }

    /// <summary>A row's interaction identity: its STABLE tag, exactly as the
    /// declared shell keyed its holders.</summary>
    private static string RowId(ShellSidebarRow row) =>
        row.Tag as string ?? row.Tag?.ToString() ?? row.Label;

    private static string HeaderId(int index) => index switch
    {
        0 => "##shell-section-0",
        1 => "##shell-section-1",
        2 => "##shell-section-2",
        3 => "##shell-section-3",
        _ => "##shell-section-" + index.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
    };

    // ── the tree ─────────────────────────────────────────────────────────

    private void DrawTree(Crystarium.ScrollRegionScope region)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset;
        float gutter = theme.Scrollbar.GutterWidth;
        // The gutter is the content's TRAILING inset, not a narrower box: a
        // row's fill bleeds under the bar while its content stops at it.
        float width = MathF.Max(1f, region.ContentWidth + gutter - inset);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(inset * scale, 0f);

        var clipper = new ImGuiListClipper();
        clipper.Begin(_slotCount, _pitch * scale);
        while (clipper.Step())
        {
            int first = _slots[Math.Clamp(clipper.DisplayStart, 0, _slotCount - 1)];
            float bottom = clipper.DisplayEnd >= _slotCount
                ? _totalHeight
                : clipper.DisplayEnd * _pitch;
            for (int i = first; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Top >= bottom)
                    break;
                Paint(in entry, origin, width, scale, theme);
            }
        }
        clipper.End();

        // The clipper's seek stops at the last whole slot; the tail band is
        // what makes the scroll extent the real content height.
        ImGui.SetCursorScreenPos(
            origin + new Vector2(-inset * scale, _totalHeight * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void Paint(
        in Entry entry, Vector2 origin, float width, float scale, Theme theme)
    {
        var at = new Vector2(origin.X, origin.Y + entry.Top * scale);
        if (entry.Kind == EntryKind.Header)
        {
            PaintHeader(in entry, at, width, scale, theme);
            return;
        }

        var row = _vm.Sections[entry.Section].Rows[entry.Row];
        var props = new TreeRowProps
        {
            Icon = row.IconName == null ? row.Icon : null,
            IconName = row.IconName,
            // Nested rows draw no mark; their guide column already spans the
            // same distance the root's icon cell does.
            HideIcon = row.Depth > 0,
            Badge = string.IsNullOrEmpty(row.Count) ? null : row.Count,
            Depth = row.Depth,
            Trunks = entry.Trunks,
            IsLastChild = row.IsLastChild,
            Expander = row.HasChildren
                ? row.Expanded
                    ? SidebarExpander.Open
                    : SidebarExpander.Collapsed
                : SidebarExpander.None,
            ExpanderDisabled = row.ExpanderDisabled,
            Selected = row.Active,
            TrailingInset = theme.Scrollbar.GutterWidth,
            ActionSlots = entry.Actions,
        };

        ImGui.SetCursorScreenPos(at);
        var action = Crystarium.TreeRow(
            entry.Id,
            row.Label,
            in props,
            out var actions,
            new ControlStyle { Width = UiWidth.Fixed(width) });
        if (entry.Actions > 0)
            PaintActions(row, entry.Id, actions, scale, theme);

        switch (action)
        {
            case TreeRowAction.Selected:
                _vm.OnRowClicked?.Invoke(row);
                break;
            case TreeRowAction.Expander:
                _vm.OnRowExpandToggled?.Invoke(row);
                // The expansion lives in the builder; the cache only has to
                // re-splice once the rebuilt rows arrive.
                _dirty = true;
                break;
            case TreeRowAction.Context:
                _vm.OnRowContextMenu?.Invoke(row);
                break;
        }
    }

    private void PaintHeader(
        in Entry entry, Vector2 at, float width, float scale, Theme theme)
    {
        var section = _vm.Sections[entry.Section];
        var style = new TextStyle
        {
            Size = theme.Typography.LabelSize,
            Weight = FontWeight.Medium,
            Color = theme.TextMuted,
        };
        Crystarium.TextInBand(
            new Vector2(
                at.X + theme.Spacing.Two * scale,
                at.Y + theme.Spacing.Two * scale),
            new Vector2(width * scale, Crystarium.MeasureText(
                section.Title, style).Y),
            section.Title,
            style,
            TextAlign.Start);

        if (!section.ShowPlus)
            return;
        // The header's plus stops at the gutter, like every row's content.
        float side = theme.Controls.SwitchHeight;
        ImGui.SetCursorScreenPos(new Vector2(
            at.X + (width - theme.Scrollbar.GutterWidth - side) * scale, at.Y));
        if (Crystarium.IconButton(
                TablerIcon.Plus,
                style: ControlStyle.Square(side),
                id: entry.Id))
            _vm.OnSectionPlus?.Invoke(entry.Section);
    }

    /// <summary>
    /// The row's action strip, seated at the origin the row reported. Outcomes
    /// are RETURNED, never handed to a callback: a click dispatches against the
    /// live row here, so the strip costs no closure per frame.
    /// </summary>
    private void PaintActions(
        ShellSidebarRow row,
        string id,
        Vector2 origin,
        float scale,
        Theme theme)
    {
        float side = theme.Controls.SwitchHeight;
        float step = (side + ActionGap) * scale;
        var square = ControlStyle.Square(side);
        ImGui.PushID(id);
        try
        {
            if (row.ActorActions)
            {
                ImGui.SetCursorScreenPos(origin);
                if (Crystarium.IconButton(
                        TablerIcon.Crosshair,
                        style: square,
                        help: "Set game target",
                        id: "##target"))
                    _vm.OnActorTarget?.Invoke(row);

                ImGui.SetCursorScreenPos(origin + new Vector2(step, 0f));
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.Eye,
                        selected: false,
                        style: square,
                        help: row.ActorVisible ? "Hide actor" : "Show actor",
                        id: "##visible",
                        slashed: !row.ActorVisible))
                    _vm.OnActorVisibility?.Invoke(row);

                ImGui.SetCursorScreenPos(origin + new Vector2(step * 2f, 0f));
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.PlayerPlay,
                        selected: false,
                        style: square,
                        help: row.ActorPaused
                            ? "Resume animation"
                            : "Pause animation",
                        id: "##pause",
                        slashed: row.ActorPaused))
                    _vm.OnActorPause?.Invoke(row);
                return;
            }

            if (row.OverlayBones is not { } bones)
                return;
            bool visible = _vm.IsOverlayVisible?.Invoke(bones) ?? true;
            ImGui.SetCursorScreenPos(origin);
            if (Crystarium.TemporaryIconToggle(
                    visible ? TablerIcon.Eye : TablerIcon.EyeOff,
                    selected: false,
                    style: square,
                    help: visible
                        ? "Hide from skeleton overlay"
                        : "Show in skeleton overlay",
                    id: "##overlay",
                    slashed: !visible))
                _vm.OnOverlayVisibility?.Invoke(row);
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
