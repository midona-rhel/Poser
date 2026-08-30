using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

/// <summary>
/// Draws the shell sidebar from a flattened structural cache. Live row state
/// is read from the current view model and expansion remains caller-owned.
/// </summary>
public sealed class ShellSidebar
{
    /// <summary>The band above the tree — the search field's row.</summary>
    private const float SearchBandHeight = 38f;

    /// <summary>The search field's own top inset inside that band.</summary>
    private const float SearchTop = 6f;

    /// <summary>Gap between row actions.</summary>
    private const float ActionGap = 2f;

    /// <summary>Selectable section pill geometry.</summary>
    private const float HeaderPillRadius = 5f;
    private const float HeaderPillInset = 1f;

    private const string SearchId = "##shell-sidebar-search";
    private const string TreeId = "##shell-sidebar-tree";
    private const string HeaderSelectId = "##select";

    private enum EntryKind : byte { Header, Row }

    /// <summary>A flattened section header or row.</summary>
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

    /// <summary>All flattened entries before filtering.</summary>
    private readonly List<Entry> _source = new();

    /// <summary>The entries the filter kept, at their vertical offsets.
    /// </summary>
    private readonly List<Entry> _entries = new();

    /// <summary>First entry intersecting each clipper slot.</summary>
    private readonly List<int> _slots = new();

    /// <summary>Per-section row counts from the last rebuild.</summary>
    private readonly List<int> _rowCounts = new();

    /// <summary>Filter scratch, parallel to <see cref="_source"/>: what the
    /// pass keeps, and the depth it kept it at.</summary>
    private bool[] _kept = new bool[64];
    private int[] _keptDepth = new int[64];

    private readonly Action<Crystarium.ScrollRegionScope> _drawTree;
    private readonly Action<string> _setSearch;

    /// <summary>The current frame's view model.</summary>
    private AppShellViewModel _vm = null!;

    /// <summary>Marks structural cache state and filter state separately.</summary>
    private bool _dirty = true;
    private bool _refilter = true;
    private ulong _revision;
    private string _filter = string.Empty;
    private string _filterLower = string.Empty;
    private float _pitch;
    private float _totalHeight;
    private int _slotCount;

    /// <summary>The current tree-guide setting.</summary>
    private bool _hideGuides;

    public ShellSidebar()
    {
        // Reuse callbacks across frames.
        _drawTree = DrawTree;
        _setSearch = next => _vm.SidebarSearch = next;
    }

    /// <summary>Draws the search band and scrollable tree.</summary>
    public void Draw(AppShellViewModel vm, Vector2 origin, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset;
        float width = size.X / scale;

        // The search ICON is the visual boundary: the pill's borderless box
        // reaches left of the inset by its own leading pad, so the glyph —
        // not invisible padding — lands on the page edge. The spawn plus
        // closes the band at the right, where the rows' span ends; the
        // pill takes what remains.
        float side = SearchBandHeight - SearchTop * 2f;
        // Not the full leading pad: the glyph should sit with the row
        // chevrons below it, a shade in from the raw page edge.
        float pillLeft = inset - theme.Controls.InputPaddingX + 4f;
        // The plus is CONTENT: it stops a gutter early, exactly where the
        // rows' own action glyphs stop — only fills reach under the gutter.
        float plusX = width - theme.Scrollbar.GutterWidth - side;
        float pillWidth = MathF.Max(
            1f, plusX - theme.Page.ActionGap - pillLeft);
        ImGui.SetCursorScreenPos(origin + new Vector2(pillLeft, SearchTop) * scale);
        Crystarium.FilterPill(
            SearchId,
            vm.SidebarSearch,
            _setSearch,
            "Search",
            ControlStyle.Workspace with { Width = UiWidth.Fixed(pillWidth) });
        ImGui.SetCursorScreenPos(origin + new Vector2(plusX, SearchTop) * scale);
        if (Crystarium.TemporaryIconToggle(
                TablerIcon.Plus,
                selected: false,
                style: ControlStyle.Square(side),
                help: "Add an actor or object to the scene",
                id: "##sidebar-spawn"))
            vm.OnSpawn?.Invoke(
                origin + new Vector2(plusX, SearchTop + side) * scale);

        Sync(vm, theme);

        // Missing configuration keeps guides visible.
        _hideGuides = Config.ConfigurationService.Instance is { } config
            && !config.Config.UI.ShowTreeGuides;

        // Search remains fixed above the scrolling tree.
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

        // A row-pitch change invalidates cached positions.
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

    /// <summary>Flattens the current section and row structure.</summary>
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
            // A title-less section contributes ROWS only: the scene tree
            // is ONE continuous list, not five kind buckets.
            if (section.Title.Length > 0)
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
                    row.ActorActions ? 4
                        : row.CameraActions ? 4
                        : row.LightActions ? 2
                        : row.GroupActions ? 1
                        : row.OverlayBones != null ? 1 : 0,
                    0f,
                    rowHeight));
            }
        }
    }

    /// <summary>
    /// Filters entries and rebuilds their offsets. Matches keep their
    /// ancestors and descendants.
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
        // Fold the tail into the final clipper slot.
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

    /// <summary>Marks matching entries while keeping every header.</summary>
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

        // A matched row keeps its descendants within the same section.
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

        // A backward pass keeps each match's ancestors.
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

    /// <summary>Converts ancestor sibling flags to the painter's trunk mask.</summary>
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

    /// <summary>Returns a stable row identity.</summary>
    private static string RowId(ShellSidebarRow row)
    {
        string key = row.Tag as string ?? row.Tag?.ToString() ?? row.Label;
        return key;
    }

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

    // ── row drag-and-drop ────────────────────────────────────────────────

    /// <summary>The row being dragged, and this frame's drop candidate.
    /// The candidate is re-derived every frame from the pointer; the drop
    /// commits on release, wherever the pointer stands.</summary>
    private ShellSidebarRow? _dragSource;
    private ShellSidebarRow? _dropTarget;
    private RowDropPosition _dropPosition;
    private ShellSidebarRow? _paintDropTarget;
    private RowDropPosition _paintDropPosition;

    private void DrawTree(Crystarium.ScrollRegionScope region)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset;
        float gutter = theme.Scrollbar.GutterWidth;
        // Row fills extend beneath the gutter while content stops before it.
        float width = MathF.Max(1f, region.ContentWidth + gutter - inset);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(inset * scale, 0f);

        // Each frame re-derives the candidate; Paint fills it back in for
        // whichever row the pointer crosses.
        _paintDropTarget = _dropTarget;
        _paintDropPosition = _dropPosition;
        _dropTarget = null;
        _dropPosition = RowDropPosition.Out;

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

        // The drag's ghost and its release, after every row painted.
        if (_dragSource is { } dragging)
        {
            // No row is the candidate: the drop lands at the END of the
            // root list, leaving any group — the caret at the tree's tail
            // says so instead of saying nothing.
            if (_paintDropTarget == null)
            {
                var tailDl = ImGui.GetWindowDrawList();
                float tailY = origin.Y + _totalHeight * scale;
                float x0 = origin.X + 6f * scale;
                float x1 = origin.X + (width - gutter) * scale;
                uint accent = ImGui.ColorConvertFloat4ToU32(theme.Accent);
                tailDl.AddRectFilled(
                    new Vector2(x0, tailY - 1f * scale),
                    new Vector2(x1, tailY + 1f * scale),
                    accent);
                float tri = 4f * scale;
                tailDl.AddTriangleFilled(
                    new Vector2(x0 - tri, tailY - tri),
                    new Vector2(x0 - tri, tailY + tri),
                    new Vector2(x0 + tri * 0.5f, tailY),
                    accent);
            }

            var mouse = ImGui.GetMousePos();
            var ghostStyle = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.LabelSize,
                Color = Crystarium.ActiveTheme.Text,
            };
            var text = _vm.DragGhostText?.Invoke(dragging) ?? dragging.Label;
            var size = Crystarium.MeasureText(text, ghostStyle);
            var pad = new Vector2(6f, 3f) * scale;
            var min = mouse + new Vector2(14f, 6f) * scale;
            ImGui.GetWindowDrawList().AddRectFilled(
                min, min + size + pad * 2f,
                ImGui.ColorConvertFloat4ToU32(
                    Crystarium.ActiveTheme.SurfaceRaised),
                Crystarium.ActiveTheme.Radii.Surface * scale);
            Crystarium.TextAt(min + pad, text, ghostStyle);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _vm.OnRowDrop?.Invoke(dragging, _dropTarget, _dropPosition);
                _dragSource = null;
                _dropTarget = null;
                _dirty = true;
            }
        }

        // The tail band preserves the exact scroll extent.
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

        // While a drag is live, the row under the pointer is the drop
        // candidate. Only a CONTAINER (a group head) takes an INTO drop,
        // and never from another container — one depth, always. Every
        // other row splits at its midline into before/after.
        if (_dragSource is { } source && !ReferenceEquals(source, row))
        {
            float rowHeight = entry.Height * scale;
            var rectMin = at;
            var rectMax = at + new Vector2(width * scale, rowHeight);
            var mouse = ImGui.GetMousePos();
            if (mouse.X >= rectMin.X && mouse.X < rectMax.X
                && mouse.Y >= rectMin.Y && mouse.Y < rectMax.Y)
            {
                bool canInto = row.DropContainer && !source.DropContainer;
                float third = rowHeight / 3f;
                _dropTarget = row;
                _dropPosition = canInto
                    ? mouse.Y < rectMin.Y + third
                        ? RowDropPosition.Before
                        : mouse.Y > rectMax.Y - third
                            ? RowDropPosition.After
                            : RowDropPosition.Into
                    : mouse.Y < rectMin.Y + rowHeight / 2f
                        ? RowDropPosition.Before
                        : RowDropPosition.After;
            }
        }

        var props = new TreeRowProps
        {
            Icon = row.IconName == null ? row.Icon : null,
            IconName = row.IconName,
            // Nested groups omit marks unless the row forces one.
            HideIcon = row.Depth > 0 && !row.ForceIcon,
            Badge = string.IsNullOrEmpty(row.Count) ? null : row.Count,
            Depth = row.Depth,
            Trunks = entry.Trunks,
            IsLastChild = row.IsLastChild,
            // Guide visibility does not change row geometry.
            HideGuides = _hideGuides,
            Expander = row.HasChildren
                ? row.Expanded
                    ? SidebarExpander.Open
                    : SidebarExpander.Collapsed
                : SidebarExpander.None,
            ExpanderDisabled = row.ExpanderDisabled,
            Selected = row.Active,
            TrailingInset = theme.Scrollbar.GutterWidth,
            ActionSlots = entry.Actions,
            Draggable = row.Draggable,
            // The row-fill highlight means INTO; an insert shows as the
            // caret line instead, and plain hover goes silent for the
            // drag's duration.
            DropTarget = ReferenceEquals(_paintDropTarget, row)
                && _paintDropPosition == RowDropPosition.Into,
            SuppressHover = _dragSource != null,
            // The game's target actor and the live camera wear the
            // current-one outline; the flags are only ever set on their
            // own row kinds.
            Marked = row.ActorTargeted || row.CameraLive,
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

        // The insert indicator: an accent line at the seam with a small
        // caret at its head, exactly where the drop will land.
        if (ReferenceEquals(_paintDropTarget, row)
            && _paintDropPosition is RowDropPosition.Before
                or RowDropPosition.After)
        {
            var dl = ImGui.GetWindowDrawList();
            float y = _paintDropPosition == RowDropPosition.Before
                ? at.Y
                : at.Y + entry.Height * scale;
            float x0 = at.X + (row.Depth * 20f + 6f) * scale;
            float x1 = at.X + (width - theme.Scrollbar.GutterWidth) * scale;
            uint accent = ImGui.ColorConvertFloat4ToU32(theme.Accent);
            dl.AddRectFilled(
                new Vector2(x0, y - 1f * scale),
                new Vector2(x1, y + 1f * scale),
                accent);
            float tri = 4f * scale;
            dl.AddTriangleFilled(
                new Vector2(x0 - tri, y - tri),
                new Vector2(x0 - tri, y + tri),
                new Vector2(x0 + tri * 0.5f, y),
                accent);
        }

        switch (action)
        {
            case TreeRowAction.Selected:
                // The release that ends a drag is the DROP, not a click.
                if (_dragSource == null)
                    _vm.OnRowClicked?.Invoke(row);
                break;
            case TreeRowAction.Drag:
                _dragSource ??= row;
                break;
            case TreeRowAction.Expander:
                _vm.OnRowExpandToggled?.Invoke(row);
                // The builder owns expansion; this only invalidates structure.
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
        // Selection is live state and does not invalidate structure.
        if (section.Selectable)
            PaintHeaderTarget(in entry, section, at, width, scale, theme);

        var style = new TextStyle
        {
            Size = theme.Typography.LabelSize,
            Weight = FontWeight.Medium,
            Color = theme.TextMuted,
        };
        // Center the title in the complete header slot.
        Crystarium.TextInBand(
            new Vector2(at.X + theme.Spacing.Two * scale, at.Y),
            new Vector2(width * scale, entry.Height * scale),
            section.Title,
            style,
            TextAlign.Start);

        if (!section.ShowPlus)
            return;
        float side = theme.Controls.SwitchHeight;
        var plus = Crystarium.SidebarTrailingAction(
            new Vector2(
                at.X + (width - theme.Scrollbar.GutterWidth) * scale,
                at.Y),
            entry.Height,
            side,
            theme.Controls.IconContentScale,
            ActionGap,
            scale);
        ImGui.SetCursorScreenPos(plus.HitMin);
        if (Crystarium.IconButton(
                TablerIcon.Plus,
                style: ControlStyle.Square(side),
                id: entry.Id,
                iconSize: plus.GlyphSide / scale))
            _vm.OnSectionPlus?.Invoke(
                entry.Section,
                plus.SpawnAnchor);
    }

    /// <summary>Draws a selectable header target using row pill geometry.</summary>
    private void PaintHeaderTarget(
        in Entry entry,
        ShellSidebarSection section,
        Vector2 at,
        float width,
        float scale,
        Theme theme)
    {
        ImGui.SetCursorScreenPos(at);
        // Scope the overlapping header target below the plus id.
        ImGui.PushID(entry.Id);
        var hit = Interactive.Reserve(
            HeaderSelectId,
            new Vector2(width * scale, entry.Height * scale),
            disabled: false);
        ImGui.PopID();
        if (section.ShowPlus)
            ImGui.SetItemAllowOverlap();

        var fill = section.Active
            ? theme.Chrome.SidebarSelected
            : hit.Hovered
                ? theme.Chrome.SidebarHover
                : Vector4.Zero;
        if (fill.W > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(
                    hit.ScreenMin.X + HeaderPillInset * scale, hit.ScreenMin.Y),
                new Vector2(
                    hit.ScreenMax.X - theme.Scrollbar.GutterWidth * scale,
                    hit.ScreenMax.Y - scale),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fill)),
                HeaderPillRadius * scale);

        if (hit.Activated)
            _vm.OnSectionSelected?.Invoke(entry.Section);
    }

    /// <summary>Draws and dispatches the row's live action strip.</summary>
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
                // The first action toggles the actor's world handle.
                bool handleShown = _vm.IsHandleShown?.Invoke(row) ?? true;
                ImGui.SetCursorScreenPos(origin);
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.ArrowsMove,
                        selected: false,
                        style: square,
                        help: handleShown
                            ? "Hide this actor's world handle"
                            : "Show this actor's world handle",
                        id: "##handle",
                        dimmed: !handleShown))
                    _vm.OnHandleToggle?.Invoke(row);

                // The crosshair marks the game's current target — accent
                // fill when this row IS it, the same voice the camera's
                // live mark speaks in.
                ImGui.SetCursorScreenPos(origin + new Vector2(step, 0f));
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.Crosshair,
                        selected: row.ActorTargeted,
                        style: square,
                        help: row.ActorTargeted
                            ? "The game's current target"
                            : "Target this actor in game",
                        id: "##target",
                        dimmed: !row.ActorTargeted))
                    _vm.OnActorTarget?.Invoke(row);

                // A faded eye means the actor is hidden.
                ImGui.SetCursorScreenPos(origin + new Vector2(step * 2f, 0f));
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.Eye,
                        selected: false,
                        style: square,
                        help: row.ActorVisible ? "Hide actor" : "Show actor",
                        id: "##visible",
                        dimmed: !row.ActorVisible))
                    _vm.OnActorVisibility?.Invoke(row);

                // The glyph reports the current animation state.
                ImGui.SetCursorScreenPos(origin + new Vector2(step * 3f, 0f));
                if (Crystarium.TemporaryIconToggle(
                        row.ActorPaused
                            ? TablerIcon.PlayerPause
                            : TablerIcon.PlayerPlay,
                        selected: false,
                        style: square,
                        help: row.ActorPaused
                            ? "Resume animation"
                            : "Pause animation",
                        id: "##pause"))
                    _vm.OnActorPause?.Invoke(row);
                return;
            }

            // Entity rows expose handle and visibility actions.
            if (row.LightActions)
            {
                bool handleShown = _vm.IsHandleShown?.Invoke(row) ?? true;
                ImGui.SetCursorScreenPos(origin);
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.ArrowsMove,
                        selected: false,
                        style: square,
                        help: handleShown
                            ? "Hide this entity's world handle"
                            : "Show this entity's world handle",
                        id: "##handle",
                        dimmed: !handleShown))
                    _vm.OnHandleToggle?.Invoke(row);

                ImGui.SetCursorScreenPos(origin + new Vector2(step, 0f));
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.Eye,
                        selected: false,
                        style: square,
                        help: row.LightOn
                            ? "Switch this off"
                            : "Switch this on",
                        id: "##light-on",
                        dimmed: !row.LightOn))
                    _vm.OnLightVisibility?.Invoke(row);
                return;
            }

            // Camera rows keep live view and edit lock beside each other.
            // A locked group's one seat: the lock itself.
            if (row.GroupActions)
            {
                ImGui.SetCursorScreenPos(origin);
                if (Crystarium.TemporaryIconToggle(
                        row.GroupLocked
                            ? TablerIcon.Lock
                            : TablerIcon.LockOpen,
                        selected: false,
                        style: square,
                        help: row.GroupLocked
                            ? "Unlock group"
                            : "Lock group — nothing in it moves",
                        id: "##group-lock",
                        dimmed: !row.GroupLocked))
                    _vm.OnGroupLock?.Invoke(row);
                return;
            }

            if (row.CameraActions)
            {
                // The recenter seat leads: the crosshair aims the camera
                // back at whoever it tracks.
                ImGui.SetCursorScreenPos(origin);
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.Crosshair,
                        selected: false,
                        style: square,
                        help: row.CameraCanRecenter
                            ? "Recenter on the tracked actor"
                            : "Nothing to recenter on right now",
                        id: "##camera-recenter",
                        dimmed: !row.CameraCanRecenter))
                    _vm.OnCameraRecenter?.Invoke(row);

                ImGui.SetCursorScreenPos(origin + new Vector2(step, 0f));
                if (Crystarium.TemporaryIconToggle(
                        TablerIcon.Video,
                        selected: row.CameraLive,
                        style: square,
                        help: row.CameraLive
                            ? "The live camera — click to return to the main camera"
                            : "Look through this camera",
                        id: "##camera-live",
                        dimmed: !row.CameraLive))
                    _vm.OnCameraLive?.Invoke(row);

                // The kind letter sits BETWEEN the seats — M main, F free,
                // C camera. A marker, not a control: it explains the row,
                // takes no click, and replaced the Default badge.
                if (row.CameraMark.Length > 0)
                {
                    var markStyle = new TextStyle
                    {
                        Size = theme.Typography.LabelSize,
                        Color = theme.TextMuted,
                    };
                    var markSize = Crystarium.MeasureText(
                        row.CameraMark, markStyle);
                    Crystarium.TextAt(
                        origin + new Vector2(
                            step * 2f + (side * scale - markSize.X) * 0.5f,
                            (side * scale - markSize.Y) * 0.5f),
                        row.CameraMark,
                        markStyle);
                }

                ImGui.SetCursorScreenPos(origin + new Vector2(step * 3f, 0f));
                if (Crystarium.TemporaryIconToggle(
                        row.CameraLocked
                            ? TablerIcon.Lock
                            : TablerIcon.LockOpen,
                        selected: false,
                        style: square,
                        help: row.CameraLocked
                            ? "Unlock camera"
                            : "Lock camera",
                        id: "##camera-lock",
                        dimmed: !row.CameraLocked))
                    _vm.OnCameraLock?.Invoke(row);
                return;
            }

            if (row.OverlayBones is not { } bones)
                return;
            // A filled pupil marks visible descendants on the inactive eye.
            int state = _vm.OverlayVisibilityOf?.Invoke(bones) ?? 2;
            ImGui.SetCursorScreenPos(origin);
            string help = state switch
            {
                0 => "Show in skeleton overlay",
                1 => "Some of this is in the overlay; show all of it",
                _ => "Hide from skeleton overlay",
            };
            bool changed = state == 1
                ? Crystarium.SidebarMixedVisibilityToggle(
                    style: square,
                    help: state == 1
                        ? "Hide the currently shown bones"
                        : help,
                    id: "##overlay")
                : Crystarium.TemporaryIconToggle(
                    TablerIcon.Eye,
                    selected: false,
                    style: square,
                    help: help,
                    id: "##overlay",
                    dimmed: state == 0);
            if (changed)
                _vm.OnOverlayVisibility?.Invoke(row);
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
