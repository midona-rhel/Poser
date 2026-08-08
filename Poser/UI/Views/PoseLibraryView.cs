using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

/// <summary>
/// A resolved thumbnail: the texture handle AND the natural pixel size the
/// aspect fit needs. A handle alone cannot be fitted — the tile square is not
/// the image's aspect — so the resolver answers both or answers nothing.
/// Shared texture wraps must be re-resolved every frame, which is why this is
/// a call rather than a stored member.
/// </summary>
public readonly record struct PoseThumbnail(nint Texture, Vector2 Size)
{
    public bool Ready => Texture != 0 && Size.X > 0f && Size.Y > 0f;
}

/// <summary>
/// One caption slot's fitted-run memo. Ellipsis fitting re-shapes the run and
/// allocates a string, and a grid caption is the COMMON truncation case — so
/// the answer is resolved once and read back on every frame that restates the
/// same run at the same band. Rows are reused in place across refilters, so the
/// source string itself is part of the key.
/// </summary>
internal struct TextFit
{
    private string? _source;
    private float _band;
    private float _size;
    private string? _fitted;

    /// <summary>Whether the last resolve had to cut the run.</summary>
    internal readonly bool Truncated => _fitted is not null;

    /// <summary>The run to draw through a Truncate constraint of
    /// <paramref name="band"/>, or null when the text fits as it stands. The
    /// key carries the EXPLICIT type size every caption slot states, so a
    /// theme whose typography moved without moving the band still re-resolves;
    /// a style that leaves its size to the theme must not be memoized here.
    /// </summary>
    internal string? Resolve(string text, in TextStyle style, float band)
    {
        float size = style.Size ?? 0f;
        if (!ReferenceEquals(_source, text) || _band != band || _size != size)
        {
            _source = text;
            _band = band;
            _size = size;
            _fitted = Crystarium.FitTruncated(text, style, band);
        }
        return _fitted;
    }
}

/// <summary>
/// One rail row: a source, a subfolder under it, or one of the two synthetic
/// heads the binder always states first — "All poses" and "Favorites". Every
/// string is minted when the rail is built, the count readout included, so no
/// frame formats one.
/// </summary>
public sealed class PoseLibraryFolderRow
{
    public required string Key;
    public required string Label;
    public required string LabelLower;
    public required int Depth;
    public int Count;
    /// <summary>The pre-minted count readout, or null to show none.</summary>
    public string? CountText;
    internal TextFit LabelFit;
}

/// <summary>
/// One tile. <see cref="Id"/> is the ImGui identity (the full path, so it
/// survives a refiltered grid), <see cref="ThumbKey"/> is what the resolver is
/// asked for, and every visible run — label, modified stamp, tags — is minted
/// with the row.
/// </summary>
public sealed class PoseLibraryTileRow
{
    public required string Id;
    public required string Label;
    public required string LabelLower;
    /// <summary>The modified stamp, already formatted ("2026-08-01 14:32").
    /// </summary>
    public required string Sub;
    public required string ThumbKey;
    public bool HasThumbnail;
    public bool Favorite;
    /// <summary>The glyph the square shows when no thumbnail resolves: the
    /// armature for a pose, the plain file mark for an Anamnesis <c>.cmp</c>,
    /// a person for a character file.</summary>
    public TablerIcon Fallback = TablerIcon.Armature;
    public string? Author;
    /// <summary>The info strip's chips. Never null; empty is the norm.
    /// </summary>
    public IReadOnlyList<string> Tags = Array.Empty<string>();
    /// <summary>Index into <see cref="PoseLibraryViewModel.Folders"/>.
    /// </summary>
    public int Folder;
    internal TextFit LabelFit;
    internal TextFit SubFit;
}

/// <summary>
/// One collapsible section of the grid — a folder, or an auto-save snapshot.
/// The binder mints the label and the count readout at refilter time and states
/// the contiguous span of <see cref="PoseLibraryViewModel.Visible"/> the group
/// covers; the grid never groups anything itself.
/// </summary>
public sealed class PoseLibraryGroupRow
{
    /// <summary>Stable across refilters — it is both the collapse key and the
    /// header's ImGui identity.</summary>
    public string Key = string.Empty;
    public string Label = string.Empty;
    /// <summary>The pre-minted count readout.</summary>
    public string CountText = string.Empty;
    public bool Collapsed;
    /// <summary>First index into <see cref="PoseLibraryViewModel.Visible"/>.
    /// </summary>
    public int Start;
    /// <summary>Visible tiles in the group; never 0 — an empty group is not
    /// stated at all.</summary>
    public int Count;
    internal TextFit LabelFit;
}

/// <summary>
/// One display band of the grid: a group header, or one row of tiles. The band
/// list is what makes the grid MIXED-height, and it is rebuilt only when the
/// visible set, the column count or the tile pitch moves — never per frame.
/// </summary>
internal readonly struct GridBand
{
    internal GridBand(int group, int start, int count, float top, float height)
    {
        Group = group;
        Start = start;
        Count = count;
        Top = top;
        Height = height;
    }

    /// <summary>Index into <see cref="PoseLibraryViewModel.Groups"/> for a
    /// header band; -1 for a tile row.</summary>
    internal readonly int Group;

    /// <summary>First index into <see cref="PoseLibraryViewModel.Visible"/>;
    /// unused by a header.</summary>
    internal readonly int Start;

    internal readonly int Count;

    /// <summary>Logical offset from the grid's content top.</summary>
    internal readonly float Top;

    internal readonly float Height;

    internal float Bottom => Top + Height;
}

public sealed class PoseLibraryViewModel
{
    /// <summary>The rail. [0] is "All poses", [1] is "Favorites", and the
    /// sources and their subfolders follow; two entries alone therefore means
    /// no source yielded anything.</summary>
    public List<PoseLibraryFolderRow> Folders = [];

    public int SelectedFolder;

    public List<PoseLibraryTileRow> Tiles = [];

    /// <summary>Indices into <see cref="Tiles"/> the query and the folder kept.
    /// Refilled in place on a change and read unchanged on every other frame.
    /// </summary>
    public List<int> Visible = [];

    /// <summary>The grid's sections, in <see cref="Visible"/> order and
    /// contiguous over it. Read only while <see cref="Grouped"/>.</summary>
    public List<PoseLibraryGroupRow> Groups = [];

    /// <summary>Whether the grid draws group headers at all. A rail folder with
    /// no subfolders is ONE group, and a lone header states nothing the rail
    /// has not already said — so that case draws flat.</summary>
    public bool Grouped;

    /// <summary>Bumped by the binder whenever <see cref="Visible"/>,
    /// <see cref="Groups"/> or a collapse state changes. The grid's band list
    /// is rebuilt off THIS, never off a per-frame comparison.</summary>
    public int LayoutRevision;

    /// <summary>Whether the folder rail is seated at all; the auto-save tab
    /// carries its structure in the grid's own headers instead.</summary>
    public bool ShowRail = true;

    /// <summary>Whether the body states the no-sources configuration answer
    /// instead of the grid.</summary>
    public bool ShowNoSources;

    /// <summary>The grid's empty caption, minted by the binder.</summary>
    public string EmptyText = "No matches.";

    /// <summary>Index into <see cref="Tiles"/>, not into
    /// <see cref="Visible"/>; -1 is no selection.</summary>
    public int Selected = -1;

    public string Query = string.Empty;

    /// <summary>The active tag filter, shown as a removable chip in the band.
    /// </summary>
    public string? ActiveTag;

    /// <summary>The tile's logical edge; the view clamps to 80..200.</summary>
    public float IconSize = 120f;

    public bool IsScanning;

    /// <summary>The footer caption. Binder-owned and pre-minted.</summary>
    public string Status = string.Empty;

    /// <summary>The primary action's caption — the binder mints
    /// "Apply to {name}".</summary>
    public string ApplyLabel = "Apply";

    public bool CanApply;
    public bool CanSpawn;

    /// <summary>Whether the toggle row above the action row shows. Character
    /// files never travel the pose import pipeline, so the MCDF tab hides it
    /// and the row's band collapses to nothing.</summary>
    public bool ShowImportToggles;

    /// <summary>The active tab's import components. Binder-owned, one set per
    /// tab; the view only states and toggles them.</summary>
    public bool ImportPosition;
    public bool ImportRotation;
    public bool ImportScale;

    /// <summary>Whether the toggle row carries the two menu buttons —
    /// import options and the bone filter — the Poses tab only.</summary>
    public bool ShowImportMenus;
    public Action? OnImportMenu;
    public Action? OnBoneFilterMenu;

    /// <summary>The band's eye: whether the user asked for a live preview at
    /// all. A PREFERENCE — the binder persists it. The preview itself is drawn
    /// by the inspector rail, not by this pane.</summary>
    public bool PreviewEnabled;

    /// <summary>Whether this tab can preview at all — binder-owned. The eye is
    /// disabled where it cannot, rather than hidden: the action cluster's
    /// geometry must not move between tabs.</summary>
    public bool PreviewAvailable;

    public Action? OnPreviewToggle;

    /// <summary>Resolves a tile's thumbnail. Called per visible tile per
    /// frame: shared texture wraps must be re-resolved, so this can never
    /// answer with a stored handle. A default answer draws the fallback glyph.
    /// </summary>
    public Func<string, PoseThumbnail>? ResolveThumbnail;

    public Action<string>? OnQuery;
    public Action<int>? OnSelectFolder;

    /// <summary>A group header's disclosure; told the index into
    /// <see cref="Groups"/>.</summary>
    public Action<int>? OnToggleGroup;

    /// <summary>Told an index into <see cref="Tiles"/>: a single click.
    /// </summary>
    public Action<int>? OnSelect;

    /// <summary>Double click, the footer's primary, and the context menu.
    /// </summary>
    public Action<int>? OnApplyTile;

    public Action<int>? OnSpawnTile;
    public Action<int>? OnToggleFavorite;

    /// <summary>The footer primary: opens the apply-target actor picker.</summary>
    public Action? OnApplyMenu;

    /// <summary>Whether tiles carry the favorite star — the poses library
    /// only; an auto-save snapshot is not a curated entry.</summary>
    public bool CanFavorite = true;

    /// <summary>The LIVE pane width while the grid is resize-stepped; the
    /// bar rows and rules track this so right-aligned clusters do not jump
    /// between steps. Zero means "same as the handed size".</summary>
    public float ChromeWidth;

    /// <summary>A tag chip; null clears the filter.</summary>
    public Action<string?>? OnTagFilter;

    public Action<float>? OnIconSize;
    public Action? OnRefresh;
    public Action? OnOpenSettings;
    public Action<bool>? OnImportPosition;
    public Action<bool>? OnImportRotation;
    public Action<bool>? OnImportScale;

    // Hoisted once per model: the frame's chrome must not mint a closure, and
    // every one of these closes over nothing but this model.
    internal Action<Crystarium.ActionBarScope>? Footer;
    internal Action<Crystarium.ActionBarScope>? FooterActions;
    internal Action<Crystarium.ScrollRegionScope>? Rail;
    internal Action<Crystarium.ScrollRegionScope>? Grid;
    internal Action? SpawnClick;
    internal Action? ApplyClick;
    internal Action? SettingsClick;
    internal Action<float>? IconSizeChange;
    internal Action<Crystarium.ActionBarScope>? Toggles;
    internal Action<bool>? PositionToggle;
    internal Action<bool>? RotationToggle;
    internal Action<bool>? ScaleToggle;
    internal Action? ImportMenuClick;
    internal Action? BoneFilterClick;
    internal Action? ApplyMenuClick;
    internal Action? PreviewToggleClick;

    // The grid's band list and the clipper's slot map — the ShellSidebar cache,
    // held on the model because the view itself is static. Rebuilt only when
    // one of the three built-* stamps stops matching.
    internal readonly List<GridBand> Bands = [];
    internal readonly List<int> Slots = [];
    internal float BandsHeight;
    internal int SlotCount;
    internal int BuiltRevision = -1;
    internal int BuiltColumns = -1;
    internal float BuiltPitch = -1f;

    // The context menu's target and its frozen rows. The array is allocated
    // with the model and REWRITTEN at open, so even the cold right-click path
    // allocates nothing: ContextMenuItem is a struct.
    internal int MenuTile = -1;
    internal bool MenuRequested;
    internal readonly ContextMenuItem[] MenuItems = new ContextMenuItem[3];
}

/// <summary>
/// The pose library, drawn INSIDE the shell's content rect: there is no window
/// and no chassis to inherit, so the view lays its own bands out in the
/// rectangle it is handed — the search band, the folder rail, the tile grid
/// with its info strip, the import-toggle row, and the action row.
///
/// <para>The rail is the SOURCE tree, which is small and never clipped; the
/// body is the tile grid, which is a catalog and always is. The body's grid
/// steps at a uniform pitch through <see cref="ImGuiListClipper"/> with an
/// inner column loop, so a thousand poses submit only the band the viewport
/// shows.</para>
/// </summary>
public static class PoseLibraryView
{
    private const string SearchId = "##pose-library-search";
    private const string RailId = "##pose-library-folders";
    private const string GridId = "##pose-library-grid";
    private const string RefreshId = "##pose-library-refresh";
    private const string ActiveTagId = "##pose-library-active-tag";
    private const string SliderId = "##pose-library-icon-size";
    private const string SettingsId = "##pose-library-open-settings";
    private const string MenuId = "##pose-library-tile-menu";
    private const string ActionRowId = "pose-library-actions";
    private const string ToggleRowId = "pose-library-import-toggles";
    private const string PreviewToggleId = "##pose-library-preview";

    // Per-tile ids. They are constants because every tile pushes its own path
    // onto the ID stack first, so the two reserves are unique per tile without
    // minting a string per tile per frame.
    private const string TileId = "##tile";
    private const string StarId = "##star";
    private const string TileLabelHelpId = "##tile-label";

    /// <summary>The band under the title bar, which is also FilterPill's own
    /// natural search height.</summary>
    private const float BandHeight = 36f;

    /// <summary>The two caption lines under a tile's thumbnail square: the
    /// name, then the modified stamp.</summary>
    private const float CaptionHeight = 34f;

    private const float LabelLineHeight = 18f;

    private const float SubLineHeight = CaptionHeight - LabelLineHeight;

    /// <summary>The strip pinned under the grid while a tile is selected. The
    /// grid's scroll region shrinks by exactly this.</summary>
    private const float InfoStripHeight = 28f;

    /// <summary>The import-toggle row seated above the action row, its top
    /// rule included. FIXED: the row never grows a second line, so the footer
    /// block's height moves only on a tab switch, never during a resize.
    /// </summary>
    private const float ToggleRowHeight = 28f;

    /// <summary>The band's, tabs' and footer's horizontal inset from the pane
    /// edge. HALF the page inset: the workspace already reads as a framed
    /// surface, so a full page inset on top doubled into pointless padding
    /// (user call, 2026-08-04). The grid's half-gutter and the rail's
    /// sidebar-contract insets are separate on purpose.</summary>
    private const float PaneInset = 6f;

    private const float MinimumIconSize = 80f;

    private const float MaximumIconSize = 200f;

    private const float SliderWidth = 120f;

    /// <summary>The grid's bar is HALF the shell gutter, and the first column
    /// breathes that same half against the body's left edge — the spawn
    /// browser's proportion.</summary>
    private const float GridBarShare = 0.5f;

    /// <summary>The grid breathes against the band above and the strip below.
    /// </summary>
    private const float GridVPad = 4f;

    /// <summary>The rail row's glyph slot: a 2px left margin, then a
    /// row-height square the small glyph centres in.</summary>
    private const float NavigationIconMargin = 2f;

    private const float NavigationPillRadius = 5f;

    /// <summary>One level of rail nesting.</summary>
    private const float FolderIndent = 12f;

    /// <summary>The group header's disclosure slot: a chevron square ahead of
    /// the title, the same proportion the rail's glyph slot uses.</summary>
    private const float HeaderChevronMargin = 2f;

    private const float HeaderPillRadius = 5f;

    /// <summary>FilterPill's own left pad; the band's margin tops it up.
    /// </summary>
    private const float SearchInnerPad = 10f;

    private static readonly Action<string> IgnoreQuery = static _ => { };

    /// <summary>The toggle row's empty left cluster: its checkboxes are all
    /// right-aligned, so the bar's content slot hosts nothing.</summary>
    private static readonly Action<Crystarium.ActionBarScope> NoContent =
        static _ => { };

    /// <summary>
    /// Fills the rectangle the shell hands the pane. The geometry is DERIVED
    /// from that rectangle and nothing else — no window size, no design size —
    /// so the library reflows with the workspace like every other pane.
    /// </summary>
    public static void Draw(PoseLibraryViewModel vm, Vector2 origin, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (!(size.X > 0f) || !(size.Y > 0f))
            return;
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;

        // Enter is sampled BEFORE the body opens its scroll child, so the gate
        // is the host window's focus rather than whichever child owns the
        // cursor. Repeat is off: a held key must not apply once per tick.
        bool submit = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && (ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false)
                || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));

        vm.Footer ??= scope => scope.Label(vm.Status);
        vm.FooterActions ??= scope => Actions(vm, scope);
        vm.Rail ??= region => DrawFolders(vm, region);
        vm.Grid ??= region => DrawGrid(vm, region);
        vm.SpawnClick ??= () =>
        {
            if (vm.Selected >= 0)
                vm.OnSpawnTile?.Invoke(vm.Selected);
        };
        vm.ApplyClick ??= () =>
        {
            if (vm.Selected >= 0)
                vm.OnApplyTile?.Invoke(vm.Selected);
        };
        vm.SettingsClick ??= () => vm.OnOpenSettings?.Invoke();
        vm.IconSizeChange ??= next => vm.OnIconSize?.Invoke(
            Math.Clamp(next, MinimumIconSize, MaximumIconSize));
        vm.Toggles ??= scope => ToggleItems(vm, scope);
        vm.PositionToggle ??= value => vm.OnImportPosition?.Invoke(value);
        vm.RotationToggle ??= value => vm.OnImportRotation?.Invoke(value);
        vm.ScaleToggle ??= value => vm.OnImportScale?.Invoke(value);
        vm.ImportMenuClick ??= () => vm.OnImportMenu?.Invoke();
        vm.BoneFilterClick ??= () => vm.OnBoneFilterMenu?.Invoke();
        vm.ApplyMenuClick ??= () => vm.OnApplyMenu?.Invoke();
        vm.PreviewToggleClick ??= () => vm.OnPreviewToggle?.Invoke();

        float chromeMaxX = origin.X
            + (vm.ChromeWidth > 0f ? vm.ChromeWidth : size.X);
        var rects = Bands(
            vm, origin, size, scale, theme, chromeMaxX, out var toggles);
        DrawBand(vm, rects.Band, scale, theme);
        DrawRail(vm, rects.Rail, scale, theme);
        DrawBody(vm, rects.Body, scale, theme);
        DrawActionRow(vm, rects.Footer, scale, theme);
        DrawMenu(vm);

        if (submit && vm.CanApply && vm.Selected >= 0
            && vm.Selected < vm.Tiles.Count)
            vm.OnApplyTile?.Invoke(vm.Selected);
    }

    /// <summary>
    /// The pane's bands, and the ink that separates them. This is pane
    /// STRUCTURE, not window chrome: two rules and the rail's raised slab, all
    /// measured from the handed rectangle. The toggle row seats between the
    /// body and the action row and shares the footer block's one separator:
    /// the body's bottom rule sits above WHATEVER the block's top band is, and
    /// no ink divides the toggle row from the action row. A hidden row
    /// collapses to nothing and the rule returns to the action row's top.
    /// </summary>
    private static WindowFrameRects Bands(
        PoseLibraryViewModel vm,
        Vector2 origin,
        Vector2 size,
        float scale,
        Theme theme,
        float chromeMaxX,
        out WindowFrameRect toggles)
    {
        // The GRID lives at the (possibly stepped) handed width; the bar
        // rows and their rules track the LIVE pane edge, so the right-
        // aligned clusters stop jumping between resize steps.
        var max = origin + size;
        float rule = MathF.Max(1f, scale);
        float bandBottom = MathF.Min(max.Y, origin.Y + BandHeight * scale);
        float rowTop = MathF.Max(
            bandBottom, max.Y - theme.Floating.ModalBarHeight * scale);
        // ONE bottom row: the import toggles share the action bar
        // (user: everything on one row), so no second band exists.
        float togglesTop = rowTop;
        // The rail never takes more than half the pane: a narrow workspace
        // keeps a grid rather than becoming a folder list.
        float railWidth = vm.ShowRail
            ? MathF.Min(theme.Settings.NavigationWidth * scale, size.X * 0.5f)
            : 0f;

        var draw = ImGui.GetWindowDrawList();
        uint separator = Packed(theme.FormSeparator);
        draw.AddRectFilled(
            new Vector2(origin.X, bandBottom - rule),
            new Vector2(chromeMaxX, bandBottom),
            separator);
        draw.AddRectFilled(
            new Vector2(origin.X, togglesTop),
            new Vector2(chromeMaxX, togglesTop + rule),
            separator);

        var rail = new WindowFrameRect(
            new Vector2(origin.X, bandBottom),
            new Vector2(origin.X + railWidth - rule, togglesTop));
        if (railWidth > rule)
        {
            draw.AddRectFilled(
                rail.Min, rail.Max, Packed(theme.SurfaceRaised));
            draw.AddRectFilled(
                new Vector2(rail.Max.X, bandBottom),
                new Vector2(rail.Max.X + rule, togglesTop),
                separator);
        }

        toggles = new WindowFrameRect(
            new Vector2(origin.X, togglesTop + rule),
            new Vector2(chromeMaxX, rowTop));
        return new WindowFrameRects
        {
            Band = new WindowFrameRect(
                origin, new Vector2(chromeMaxX, bandBottom - rule)),
            Rail = rail,
            Body = new WindowFrameRect(
                new Vector2(origin.X + railWidth, bandBottom),
                new Vector2(max.X, togglesTop)),
            Footer = new WindowFrameRect(
                new Vector2(
                    origin.X,
                    togglesTop < rowTop ? rowTop : rowTop + rule),
                new Vector2(chromeMaxX, max.Y)),
        };
    }

    /// <summary>The import-toggle row: Position / Rotation / Scale, right-
    /// aligned against the same inset the action row's cluster ends at.
    /// </summary>
    private static void DrawToggleRow(
        PoseLibraryViewModel vm, WindowFrameRect row, float scale)
    {
        if (!vm.ShowImportToggles
            || !(row.Size.X > 0f) || !(row.Size.Y > 0f))
            return;
        float inset = PaneInset * scale;
        Crystarium.ActionBar(
            ToggleRowId,
            new Vector2(row.Min.X + inset, row.Min.Y),
            new Vector2(
                MathF.Max(0f, row.Size.X - inset * 2f), row.Size.Y),
            NoContent,
            vm.Toggles,
            ActionBarSeparator.None);
    }

    private static void ToggleItems(
        PoseLibraryViewModel vm, Crystarium.ActionBarScope scope)
    {
        scope.Checkbox("Position", vm.ImportPosition, vm.PositionToggle!);
        scope.Checkbox("Rotation", vm.ImportRotation, vm.RotationToggle!);
        scope.Checkbox("Scale", vm.ImportScale, vm.ScaleToggle!);
        // The import menu, opened from this row (the user's placement); the
        // bone filter opens from its button INSIDE that menu.
        if (vm.ShowImportMenus)
            scope.Button("Options", vm.ImportMenuClick!);
    }

    /// <summary>The action row: the status caption on the left (the size
    /// scrubber is seated beside it separately) and the three commands on the
    /// right.</summary>
    private static void DrawActionRow(
        PoseLibraryViewModel vm, WindowFrameRect footer, float scale, Theme theme)
    {
        if (!(footer.Size.X > 0f) || !(footer.Size.Y > 0f))
            return;
        float inset = PaneInset * scale;
        Crystarium.ActionBar(
            ActionRowId,
            new Vector2(footer.Min.X + inset, footer.Min.Y),
            new Vector2(
                MathF.Max(0f, footer.Size.X - inset * 2f), footer.Size.Y),
            vm.Footer!,
            vm.FooterActions,
            ActionBarSeparator.None);
    }

    private static void Actions(
        PoseLibraryViewModel vm, Crystarium.ActionBarScope scope)
    {
        // The import components and the two menus lead the ONE bottom row.
        if (vm.ShowImportToggles)
        {
            scope.Checkbox("Position", vm.ImportPosition, vm.PositionToggle!);
            scope.Checkbox("Rotation", vm.ImportRotation, vm.RotationToggle!);
            scope.Checkbox("Scale", vm.ImportScale, vm.ScaleToggle!);
        }
        if (vm.ShowImportMenus)
            scope.Button("Options", vm.ImportMenuClick!);
        bool none = vm.Selected < 0 || vm.Selected >= vm.Tiles.Count;
        // Default control scale, the same the toggle row's Options button
        // wears (user: Comfortable read oversized here). Configuring
        // sources belongs where the library is, not only in the empty
        // state a user with sources never sees.
        scope.Button(
            "Add source",
            vm.SettingsClick!);
        scope.Button(
            "Spawn as new",
            vm.SpawnClick!,
            disabled: none || !vm.CanSpawn);
        // The primary opens the ACTOR PICKER — the pose applies to whoever
        // is chosen there, not silently to the selection.
        scope.Button(
            vm.ApplyLabel,
            vm.ApplyMenuClick!,
            disabled: none || !vm.CanApply,
            variant: ButtonVariant.Primary);
    }

    // ---- Band -------------------------------------------------------

    /// <summary>The band's content: <see cref="Bands"/> owns the band and its
    /// rule, this owns the field, the rescan affordance and the tag chip.
    /// The right cluster is measured first, so the search takes exactly what
    /// is left.</summary>
    private static void DrawBand(
        PoseLibraryViewModel vm, WindowFrameRect band, float scale, Theme theme)
    {
        if (!(band.Size.X > 0f))
            return;
        float inset = PaneInset * scale;
        float gap = theme.Page.ActionGap * scale;
        float action = theme.Floating.CloseActionSize;
        float actionPx = action * scale;

        float right = band.Max.X - inset;
        ImGui.SetCursorScreenPos(new Vector2(
            right - actionPx,
            band.Min.Y + (band.Size.Y - actionPx) * 0.5f));
        Crystarium.IconButton(
            TablerIcon.Refresh,
            vm.OnRefresh,
            style: ControlStyle.Square(action),
            disabled: vm.IsScanning,
            help: "Rescan source folders",
            id: RefreshId);
        right -= actionPx + gap;

        // The preview's own switch, beside the rescan. The preview itself
        // seats in the INSPECTOR rail; this is only its switch, DISABLED
        // rather than hidden on the tab that cannot preview (character files
        // never travel the import pipeline) so the cluster's geometry does not
        // move between tabs.
        ImGui.SetCursorScreenPos(new Vector2(
            right - actionPx,
            band.Min.Y + (band.Size.Y - actionPx) * 0.5f));
        Crystarium.TemporaryIconToggle(
            TablerIcon.Eye,
            vm.PreviewEnabled,
            vm.PreviewToggleClick,
            ControlStyle.Square(action),
            disabled: !vm.PreviewAvailable,
            help: vm.PreviewEnabled
                ? "Stop previewing the selected pose"
                : "Preview the selected pose on a hidden actor",
            id: PreviewToggleId,
            slashed: !vm.PreviewEnabled);
        right -= actionPx + gap;

        // The size scrubber rides the band, not the footer: the footer's
        // buttons overflowed it at minimal width (user call).
        if (right - SliderWidth * scale
            > band.Min.X + band.Size.X * 0.5f)
        {
            float sliderHeight = theme.Controls.SliderHeight * scale;
            ImGui.SetCursorScreenPos(new Vector2(
                right - SliderWidth * scale,
                band.Min.Y + (band.Size.Y - sliderHeight) * 0.5f));
            Crystarium.Slider(
                SliderId,
                Math.Clamp(vm.IconSize, MinimumIconSize, MaximumIconSize),
                MinimumIconSize,
                MaximumIconSize,
                vm.IconSizeChange!,
                new ControlStyle { Width = UiWidth.Fixed(SliderWidth) },
                help: "Thumbnail size");
            right -= SliderWidth * scale + gap;
        }

        if (vm.ActiveTag is { Length: > 0 } tag)
            right = DrawActiveTag(vm, tag, band, right, scale, theme)
                - gap;

        // The margin makes up FilterPill's own pad, so the search glyph sits
        // where the rail's row marks do. The type tabs live in the SHELL's
        // tab strip, not here — the band is the search field's.
        float left = band.Min.X
            + MathF.Max(0f, inset - SearchInnerPad * scale);
        float width = (right - left) / scale;
        if (!(width > 0f))
            return;
        ImGui.SetCursorScreenPos(new Vector2(left, band.Min.Y));
        Crystarium.FilterPill(
            SearchId,
            vm.Query,
            vm.OnQuery ?? IgnoreQuery,
            "Search poses",
            new ControlStyle { Width = UiWidth.Fixed(width) });
    }

    /// <summary>The removable tag chip: the whole pill is the clear target,
    /// the cross only says so. Returns its left edge.</summary>
    private static float DrawActiveTag(
        PoseLibraryViewModel vm,
        string tag,
        WindowFrameRect band,
        float right,
        float scale,
        Theme theme)
    {
        var style = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.Accent,
        };
        float text = Crystarium.MeasureText(tag, style).X;
        float glyph = theme.Controls.SmallIconSize * scale;
        float padX = theme.Spacing.Three * scale;
        float inner = theme.Spacing.Two * scale;
        float width = padX + text + inner + glyph + padX;
        float height = theme.Controls.WorkspaceHeight * scale;
        var min = new Vector2(
            right - width, band.Min.Y + (band.Size.Y - height) * 0.5f);
        if (min.X <= band.Min.X)
            return right;

        ImGui.SetCursorScreenPos(min);
        var hit = Interactive.Reserve(
            ActiveTagId, new Vector2(width, height), disabled: false);
        ImGui.GetWindowDrawList().AddRectFilled(
            min,
            min + new Vector2(width, height),
            Packed(hit.Hovered
                ? theme.Chrome.AccentFillBorder
                : theme.Chrome.AccentFill),
            theme.Radii.Pill * scale);
        Crystarium.TextInBand(
            new Vector2(min.X + padX, min.Y),
            new Vector2(text, height),
            tag,
            style,
            TextAlign.Start,
            besideIcon: true);
        var glyphMin = new Vector2(
            min.X + width - padX - glyph, min.Y + (height - glyph) * 0.5f);
        Crystarium.IconIn(
            glyphMin, glyphMin + new Vector2(glyph), TablerIcon.X, theme.Accent);

        if (hit.Hovered)
            Crystarium.HoverHelp.Explain(
                ActiveTagId, min, min + new Vector2(width, height),
                "Clear the tag filter");
        if (hit.Clicked)
            vm.OnTagFilter?.Invoke(null);
        return min.X;
    }

    // ---- Rail -------------------------------------------------------

    /// <summary>The rail's content: <see cref="Bands"/> owns the slab and its
    /// rule, this owns the inset and the rows. Sources are a handful of folders, never a
    /// catalog, so the rows submit unclipped — the ONE place in this view that
    /// is true.</summary>
    private static void DrawRail(
        PoseLibraryViewModel vm, WindowFrameRect rail, float scale, Theme theme)
    {
        if (!(rail.Size.X > 0f))
            return;
        float inset = theme.Page.Inset;
        // The region spans the FULL rail width: the gutter is the rows'
        // trailing inset (ShellSidebar.DrawTree contract), not a second
        // horizontal margin, so only the vertical inset is applied here.
        ImGui.SetCursorScreenPos(
            new Vector2(rail.Min.X, rail.Min.Y + inset * scale));
        Crystarium.ScrollRegion(
            RailId,
            rail.Size.X / scale,
            rail.Size.Y / scale - inset * 2f,
            vm.Rail!);
    }

    private static void DrawFolders(
        PoseLibraryViewModel vm, Crystarium.ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset;
        float gutter = theme.Scrollbar.GutterWidth;
        // The gutter is the rows' TRAILING inset, not a narrower box: the row
        // box bleeds under the scroll bar while the pill and its content stop
        // a gutter early, so the visible right separation matches the left
        // inset whether or not the bar is shown.
        float width =
            MathF.Max(1f, region.ContentWidth + gutter - inset) * scale;
        var origin = ImGui.GetCursorScreenPos() + new Vector2(inset * scale, 0f);

        // The rows stack flush at the row height: the ambient vertical spacing
        // belongs to the surrounding flow, not to the rail.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            for (int i = 0; i < vm.Folders.Count; i++)
            {
                var pos = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new Vector2(origin.X, pos.Y));
                if (FolderRow(
                        vm.Folders[i],
                        // The two synthetic heads are positional by contract,
                        // so the favourites row can carry its own mark without
                        // the binder having to state one per row.
                        i == 1 ? TablerIcon.Star : TablerIcon.Folder,
                        vm.SelectedFolder == i,
                        width,
                        gutter * scale,
                        scale,
                        theme))
                    vm.OnSelectFolder?.Invoke(i);
            }
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>One rail row, drawn from primitives for the same reason the
    /// settings rail is: its pill runs flush to the row box and its glyph is
    /// full opacity, which is not the tree row's shape.</summary>
    private static bool FolderRow(
        PoseLibraryFolderRow row,
        TablerIcon icon,
        bool selected,
        float width,
        float trailingInset,
        float scale,
        Theme theme)
    {
        float height = theme.Controls.ListRowHeight * scale;
        var hit = Interactive.Reserve(
            row.Key, new Vector2(width, height), disabled: false);

        // The row box bleeds under the scroll gutter; the pill and everything
        // inside it stop here instead (TreeRow's TrailingInset contract).
        float contentRight = hit.ScreenMax.X - trailingInset;

        var fill = selected
            ? theme.Chrome.SidebarSelected
            : hit.Hovered
                ? theme.Chrome.SidebarHover
                : Vector4.Zero;
        if (fill.W > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(
                hit.ScreenMin,
                new Vector2(contentRight, hit.ScreenMax.Y),
                ImGui.ColorConvertFloat4ToU32(fill),
                NavigationPillRadius * scale);

        float glyph = theme.Controls.SmallIconSize * scale;
        var slotMin = new Vector2(
            hit.ScreenMin.X
                + (NavigationIconMargin + row.Depth * FolderIndent) * scale,
            hit.ScreenMin.Y);
        var glyphMin = slotMin + new Vector2((height - glyph) * 0.5f);
        Crystarium.IconIn(glyphMin, glyphMin + new Vector2(glyph), icon);

        float labelX = slotMin.X + height;
        // The count breathes off the pill's right edge; the label in turn
        // breathes off the count.
        float labelRight = contentRight - theme.Spacing.Four * scale;
        if (row.CountText is { Length: > 0 } count)
        {
            var countStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Family = FontFamily.Mono,
                Color = theme.FormLabel,
            };
            float countWidth = Crystarium.MeasureText(count, countStyle).X;
            labelRight -= countWidth;
            Crystarium.TextInBand(
                new Vector2(labelRight, hit.ScreenMin.Y),
                new Vector2(countWidth, height),
                count,
                countStyle,
                TextAlign.Start,
                besideIcon: true);
            labelRight -= theme.Spacing.Three * scale;
        }

        Fitted(
            ref row.LabelFit,
            new Vector2(labelX, hit.ScreenMin.Y),
            new Vector2(labelRight - labelX, height),
            row.Label,
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Color = theme.Text,
            });
        return hit.Activated;
    }

    // ---- Body -------------------------------------------------------

    private static void DrawBody(
        PoseLibraryViewModel vm, WindowFrameRect body, float scale, Theme theme)
    {
        if (!(body.Size.X > 0f) || !(body.Size.Y > 0f))
            return;

        // No source ever yielded a folder, which is a CONFIGURATION answer
        // rather than an empty filter. The binder decides it: the auto-save tab
        // browses no configured source at all.
        if (vm.ShowNoSources)
        {
            DrawNoSources(vm, body, scale, theme);
            return;
        }

        bool selected = vm.Selected >= 0 && vm.Selected < vm.Tiles.Count;
        float strip = selected ? InfoStripHeight : 0f;
        ImGui.SetCursorScreenPos(body.Min);
        Crystarium.ScrollRegion(
            GridId,
            body.Size.X / scale,
            body.Size.Y / scale - strip,
            vm.Grid!,
            theme.Scrollbar.GutterWidth * GridBarShare);

        if (selected)
            DrawInfoStrip(
                vm,
                new WindowFrameRect(
                    new Vector2(body.Min.X, body.Max.Y - strip * scale),
                    body.Max),
                scale,
                theme);
    }

    /// <summary>The configuration empty state: the honest reason, and the one
    /// affordance that fixes it.</summary>
    private static void DrawNoSources(
        PoseLibraryViewModel vm, WindowFrameRect body, float scale, Theme theme)
    {
        var style = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.FormHint,
        };
        float row = theme.Controls.ListRowHeight * scale;
        float button = theme.Controls.ComfortableHeight * scale;
        float block = row + theme.Spacing.Three * scale + button;
        float top = body.Min.Y + (body.Size.Y - block) * 0.5f;

        Crystarium.TextInBand(
            new Vector2(body.Min.X, top),
            new Vector2(body.Size.X, row),
            "No pose sources found.",
            style,
            TextAlign.Center);

        float width = Crystarium.MeasureButton(
            "Open Settings", ControlStyle.Comfortable).X;
        ImGui.SetCursorScreenPos(new Vector2(
            body.Min.X + (body.Size.X - width) * 0.5f,
            top + row + theme.Spacing.Three * scale));
        Crystarium.Button(
            "Open Settings",
            vm.OnOpenSettings,
            style: ControlStyle.Comfortable,
            id: SettingsId);
    }

    /// <summary>
    /// The grid, drawn from the band list. Headers make the content MIXED
    /// height, which the clipper cannot step through directly — so it steps a
    /// uniform grid of tile-row slots and the slot map turns its reported band
    /// into a band range, exactly as <see cref="ShellSidebar"/> does for its
    /// tree.
    /// </summary>
    private static void DrawGrid(
        PoseLibraryViewModel vm, Crystarium.ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Scrollbar.GutterWidth * GridBarShare;
        float gap = theme.Spacing.Three;
        float icon = Math.Clamp(
            vm.IconSize, MinimumIconSize, MaximumIconSize);
        float pitchX = icon + gap;
        float pitchY = icon + CaptionHeight + gap;
        float usable = MathF.Max(0f, region.ContentWidth - inset);
        // The last column carries no trailing gap, so it is added back before
        // the division rather than subtracted from every pitch.
        int columns = Math.Max(1, (int)MathF.Floor((usable + gap) / pitchX));
        BuildBands(vm, columns, pitchY, theme);

        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            float pad = GridVPad * scale;
            ImGui.Dummy(new Vector2(0f, pad));
            if (vm.Visible.Count == 0)
            {
                EmptyLine(vm.EmptyText, region.ContentWidth, scale, theme);
            }
            else
            {
                var origin = ImGui.GetCursorScreenPos();
                // A header's box runs from the first tile column to under the
                // scroll bar; its own content stops a gutter early.
                float width = MathF.Max(1f, usable + inset) * scale;
                var clipper = new ImGuiListClipper();
                clipper.Begin(vm.SlotCount, pitchY * scale);
                while (clipper.Step())
                {
                    int first = vm.Slots[
                        Math.Clamp(clipper.DisplayStart, 0, vm.SlotCount - 1)];
                    float bottom = clipper.DisplayEnd >= vm.SlotCount
                        ? vm.BandsHeight
                        : clipper.DisplayEnd * pitchY;
                    for (int i = first; i < vm.Bands.Count; i++)
                    {
                        var band = vm.Bands[i];
                        if (band.Top >= bottom)
                            break;
                        PaintBand(
                            vm, in band, origin, width, inset, pitchX, icon,
                            scale, theme);
                    }
                }
                clipper.End();

                // The clipper's seek stops at the last whole slot; this is what
                // makes the scroll extent the real content height.
                ImGui.SetCursorScreenPos(
                    origin + new Vector2(0f, vm.BandsHeight * scale));
                ImGui.Dummy(Vector2.Zero);
            }
            // Trailing breathing is INVISIBLE to ImGui's scroll extent — no
            // item covers it — so max-scroll would pin the last row to the
            // viewport edge without this.
            ImGui.Dummy(new Vector2(0f, pad));
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private static void PaintBand(
        PoseLibraryViewModel vm,
        in GridBand band,
        Vector2 origin,
        float width,
        float inset,
        float pitchX,
        float icon,
        float scale,
        Theme theme)
    {
        var at = new Vector2(origin.X, origin.Y + band.Top * scale);
        if (band.Group >= 0)
        {
            GroupHeader(
                vm, band.Group,
                new Vector2(at.X + inset * scale, at.Y),
                width, inset, scale, theme);
            return;
        }

        for (int c = 0; c < band.Count; c++)
            Tile(
                vm,
                vm.Visible[band.Start + c],
                new Vector2(at.X + (inset + c * pitchX) * scale, at.Y),
                icon,
                scale,
                theme);
    }

    /// <summary>
    /// One collapsible group header: the section language the shell sidebar
    /// uses, scaled to the grid. The box bleeds under the scroll bar while the
    /// pill and its content stop a gutter early, and the count breathes off
    /// that edge — the rail's rule, applied here.
    /// </summary>
    private static void GroupHeader(
        PoseLibraryViewModel vm,
        int index,
        Vector2 min,
        float width,
        float trailingInset,
        float scale,
        Theme theme)
    {
        var group = vm.Groups[index];
        float height = theme.Floating.CloseActionSize * scale;
        ImGui.SetCursorScreenPos(min);
        var hit = Interactive.Reserve(
            group.Key, new Vector2(width, height), disabled: false);
        float contentRight = hit.ScreenMax.X - trailingInset * scale;

        if (hit.Hovered)
            ImGui.GetWindowDrawList().AddRectFilled(
                hit.ScreenMin,
                new Vector2(contentRight, hit.ScreenMax.Y),
                Packed(theme.Chrome.SidebarHover),
                HeaderPillRadius * scale);

        float glyph = theme.Controls.SmallIconSize * scale;
        var slotMin = new Vector2(
            hit.ScreenMin.X + HeaderChevronMargin * scale, hit.ScreenMin.Y);
        var glyphMin = slotMin + new Vector2((height - glyph) * 0.5f);
        Crystarium.IconIn(
            glyphMin,
            glyphMin + new Vector2(glyph),
            group.Collapsed ? TablerIcon.ChevronRight : TablerIcon.ChevronDown,
            theme.TextMuted);

        float labelX = slotMin.X + height;
        float labelRight = contentRight - theme.Spacing.Four * scale;
        var countStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Family = FontFamily.Mono,
            Color = theme.FormLabel,
        };
        float countWidth = Crystarium.MeasureText(group.CountText, countStyle).X;
        labelRight -= countWidth;
        Crystarium.TextInBand(
            new Vector2(labelRight, hit.ScreenMin.Y),
            new Vector2(countWidth, height),
            group.CountText,
            countStyle,
            TextAlign.Start,
            besideIcon: true);
        labelRight -= theme.Spacing.Three * scale;

        Fitted(
            ref group.LabelFit,
            new Vector2(labelX, hit.ScreenMin.Y),
            new Vector2(labelRight - labelX, height),
            group.Label,
            new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Weight = FontWeight.Medium,
                Color = theme.TextMuted,
            });

        if (hit.Activated)
            vm.OnToggleGroup?.Invoke(index);
    }

    /// <summary>
    /// The band list and the clipper's slot map. Rebuilt ONLY when the binder
    /// says the visible set moved, or when the grid's own geometry did — a warm
    /// frame reads both lists unchanged.
    /// </summary>
    private static void BuildBands(
        PoseLibraryViewModel vm, int columns, float pitchY, Theme theme)
    {
        if (vm.BuiltRevision == vm.LayoutRevision
            && vm.BuiltColumns == columns
            && vm.BuiltPitch == pitchY)
            return;
        vm.BuiltRevision = vm.LayoutRevision;
        vm.BuiltColumns = columns;
        vm.BuiltPitch = pitchY;

        var bands = vm.Bands;
        bands.Clear();
        float y = 0f;
        if (!vm.Grouped)
        {
            y = TileBands(bands, 0, vm.Visible.Count, columns, pitchY, y);
        }
        else
        {
            float header = theme.Floating.CloseActionSize;
            float lead = theme.Spacing.Four;
            for (int g = 0; g < vm.Groups.Count; g++)
            {
                var group = vm.Groups[g];
                if (group.Count <= 0)
                    continue;
                if (bands.Count > 0)
                    y += lead;
                bands.Add(new GridBand(g, 0, 0, y, header));
                y += header;
                if (group.Collapsed)
                    continue;
                y = TileBands(
                    bands, group.Start, group.Count, columns, pitchY, y);
            }
        }

        vm.BandsHeight = y;
        // The clipper's grid is the TILE row pitch, and the tail is folded into
        // the last slot, so its seek can never overshoot the content height.
        vm.SlotCount = Math.Max(1, (int)(y / pitchY));
        var slots = vm.Slots;
        slots.Clear();
        int at = 0;
        for (int slot = 0; slot < vm.SlotCount; slot++)
        {
            float top = slot * pitchY;
            while (at + 1 < bands.Count && bands[at].Bottom <= top)
                at++;
            slots.Add(at);
        }
    }

    private static float TileBands(
        List<GridBand> bands,
        int start,
        int count,
        int columns,
        float pitchY,
        float y)
    {
        for (int i = 0; i < count; i += columns)
        {
            bands.Add(new GridBand(
                -1, start + i, Math.Min(columns, count - i), y, pitchY));
            y += pitchY;
        }
        return y;
    }

    /// <summary>
    /// One tile. Two reserves, the STAR FIRST: <see cref="Interactive"/>
    /// defers overlap entirely to ImGui's own hovered-id rule, so submission
    /// order alone would leave the outcome at that rule's mercy. The star's
    /// rectangle is therefore also tested geometrically, and every outcome is
    /// routed through that test — the favourite toggle wins its own square
    /// under EITHER policy, and the tile keeps its hover fill either way.
    /// </summary>
    private static void Tile(
        PoseLibraryViewModel vm,
        int index,
        Vector2 min,
        float icon,
        float scale,
        Theme theme)
    {
        var tile = vm.Tiles[index];
        var size = new Vector2(icon, icon + CaptionHeight) * scale;
        var draw = ImGui.GetWindowDrawList();

        // The tile's path seeds the ID stack, so the two reserves below need
        // no per-tile string and still hash to a per-tile identity.
        ImGui.PushID(tile.Id);
        try
        {
            float star = theme.Controls.SmallIconSize * scale;
            float pad = theme.Spacing.Two * scale;
            var starMin = new Vector2(min.X + size.X - pad - star, min.Y + pad);
            var starMax = starMin + new Vector2(star);

            ImGui.SetCursorScreenPos(starMin);
            var starHit = Interactive.Reserve(
                StarId, new Vector2(star), disabled: false);
            ImGui.SetCursorScreenPos(min);
            var hit = Interactive.Reserve(TileId, size, disabled: false);

            bool onStar = ImGui.IsMouseHoveringRect(starMin, starMax);
            bool hovered = hit.Hovered || starHit.Hovered;
            bool selected = vm.Selected == index;

            var fill = selected
                ? theme.Chrome.SidebarSelected
                : hovered
                    ? theme.Chrome.WeakOverlay
                    : Vector4.Zero;
            float radius = theme.Radii.Medium * scale;
            if (fill.W > 0f)
                draw.AddRectFilled(min, min + size, Packed(fill), radius);
            if (selected)
            {
                float half = 0.5f * scale;
                draw.AddRect(
                    min + new Vector2(half),
                    min + size - new Vector2(half),
                    Packed(theme.Accent),
                    MathF.Max(0f, radius - half),
                    ImDrawFlags.None,
                    scale);
            }

            DrawThumbnail(vm, tile, min, size, icon, pad, scale, theme);
            DrawCaption(tile, min, size, icon, pad, hovered, scale, theme);

            if (tile.Favorite && vm.CanFavorite)
            {
                // A favorite is FILLED and warning-yellow; the stroked icon
                // in the same color rides on top so the perimeter keeps the
                // icon pipeline's anti-aliasing.
                FilledStar(draw, starMin, starMax, Packed(theme.Warning));
                Crystarium.IconIn(
                    starMin, starMax, TablerIcon.Star, theme.Warning);
            }
            else if (hovered && vm.CanFavorite)
                Crystarium.IconIn(
                    starMin,
                    starMax,
                    TablerIcon.Star,
                    theme.TextMuted,
                    opacity: onStar ? 1f : 0.8f);

            if (onStar && vm.CanFavorite)
            {
                if (hit.Clicked || starHit.Clicked)
                    vm.OnToggleFavorite?.Invoke(index);
                return;
            }
            if (hit.Clicked)
                vm.OnSelect?.Invoke(index);
            if (hit.DoubleClicked)
                vm.OnApplyTile?.Invoke(index);
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                vm.MenuTile = index;
                vm.MenuRequested = true;
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>The filled favorite mark. The icon pipeline strokes SVG paths
    /// and cannot fill, and the draw list fills only convex polygons — but a
    /// star polygon is star-shaped from its centroid, so a triangle fan from
    /// the centre fills it exactly. The SVG glyph's 24-grid star spans radius
    /// ~9.75 of 12, so the fan uses the same fraction of the box.</summary>
    private static void FilledStar(
        ImDrawListPtr draw, Vector2 min, Vector2 max, uint color)
    {
        var centre = (min + max) * 0.5f
            + new Vector2(0f, (max.Y - min.Y) * 0.02f);
        float outer = (max.X - min.X) * 0.5f * 0.8125f;
        float inner = outer * 0.475f;
        Span<Vector2> points = stackalloc Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float radius = (i & 1) == 0 ? outer : inner;
            float angle = -MathF.PI / 2f + i * (MathF.PI / 5f);
            points[i] = new Vector2(
                centre.X + MathF.Cos(angle) * radius,
                centre.Y + MathF.Sin(angle) * radius);
        }
        for (int i = 0; i < 10; i++)
            draw.AddTriangleFilled(
                centre, points[i], points[(i + 1) % 10], color);
    }

    /// <summary>The thumbnail square, aspect-fitted and centred exactly as the
    /// file dialog's preview column fits its image: a pose renders portrait or
    /// landscape and the square has to seat either without cropping. An
    /// unresolved thumbnail draws the fallback glyph instead — legacy poses
    /// take the plain file mark, everything else the armature.</summary>
    private static void DrawThumbnail(
        PoseLibraryViewModel vm,
        PoseLibraryTileRow tile,
        Vector2 min,
        Vector2 size,
        float icon,
        float pad,
        float scale,
        Theme theme)
    {
        var boxMin = min + new Vector2(pad);
        var boxMax = new Vector2(
            min.X + size.X - pad, min.Y + icon * scale - pad);
        var box = boxMax - boxMin;
        if (!(box.X > 0f) || !(box.Y > 0f))
            return;

        PoseThumbnail thumb =
            tile.HasThumbnail && vm.ResolveThumbnail is { } resolve
                ? resolve(tile.ThumbKey)
                : default;
        if (thumb.Ready)
        {
            float fit = MathF.Min(box.X / thumb.Size.X, box.Y / thumb.Size.Y);
            var fitted = thumb.Size * fit;
            var imageMin = theme.Optical.Snap(boxMin + (box - fitted) * 0.5f);
            ImGui.GetWindowDrawList().AddImage(
                new ImTextureID(thumb.Texture),
                imageMin,
                imageMin + fitted,
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Vector4.One)));
            return;
        }

        // The glyph edge is BUCKETED to 8px steps: the icon cache bakes per
        // exact pixel size and paints a first-seen size in software, so a
        // size-slider drag that grew the glyph every frame re-rasterized it
        // every frame and cost whole frames. Within a bucket the key repeats
        // and the draw stays a cached texture.
        float bucket = 8f * scale;
        float side = MathF.Max(
            bucket,
            MathF.Floor(MathF.Min(box.X, box.Y) * 0.4f / bucket) * bucket);
        var glyphMin = theme.Optical.Snap(
            boxMin + (box - new Vector2(side)) * 0.5f);
        Crystarium.IconIn(
            glyphMin, glyphMin + new Vector2(side), tile.Fallback, theme.TextDim);
    }

    /// <summary>The tile's two caption lines: the name, then the modified
    /// stamp. The full name is previewed on hover ONLY when it was cut — the
    /// card would otherwise repeat what is already legible.</summary>
    private static void DrawCaption(
        PoseLibraryTileRow tile,
        Vector2 min,
        Vector2 size,
        float icon,
        float pad,
        bool hovered,
        float scale,
        Theme theme)
    {
        float band = size.X - pad * 2f;
        if (!(band > 0f))
            return;
        float top = min.Y + icon * scale;
        var labelStyle = new TextStyle
        {
            Size = theme.Typography.BodySize,
            Color = theme.Text,
        };
        Fitted(
            ref tile.LabelFit,
            new Vector2(min.X + pad, top),
            new Vector2(band, LabelLineHeight * scale),
            tile.Label,
            labelStyle,
            TextAlign.Center,
            besideIcon: false);

        Fitted(
            ref tile.SubFit,
            new Vector2(min.X + pad, top + LabelLineHeight * scale),
            new Vector2(band, SubLineHeight * scale),
            tile.Sub,
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            },
            TextAlign.Center,
            besideIcon: false);

        // The fit already knows whether the name was cut, so the card comes
        // off that answer instead of a second measure.
        if (hovered && tile.LabelFit.Truncated)
            Crystarium.HoverHelp.Preview(
                TileLabelHelpId, min, min + size, tile.Label);
    }

    /// <summary>The grid's empty state: one caption on a row band, centred
    /// over the columns that would have been there.</summary>
    private static void EmptyLine(
        string text, float contentWidth, float scale, Theme theme)
    {
        var min = ImGui.GetCursorScreenPos();
        float height = theme.Controls.ListRowHeight * scale;
        Crystarium.TextInBand(
            min,
            new Vector2(contentWidth * scale, height),
            text,
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            },
            TextAlign.Center);
        ImGui.Dummy(new Vector2(0f, height));
    }

    // ---- Info strip -------------------------------------------------

    /// <summary>
    /// The selected pose's metadata, pinned under the grid: the author, then
    /// its tags as chips that filter. Every run is drawn as its OWN text item
    /// — an author and a tag are never concatenated, so no frame builds a
    /// string.
    /// </summary>
    private static void DrawInfoStrip(
        PoseLibraryViewModel vm,
        WindowFrameRect strip,
        float scale,
        Theme theme)
    {
        var tile = vm.Tiles[vm.Selected];
        var draw = ImGui.GetWindowDrawList();
        float rule = MathF.Max(1f, scale);
        draw.AddRectFilled(
            strip.Min,
            new Vector2(strip.Max.X, strip.Min.Y + rule),
            Packed(theme.FormSeparator));

        float inset = theme.Scrollbar.GutterWidth * GridBarShare * scale;
        float gap = theme.Spacing.Three * scale;
        float x = strip.Min.X + inset;
        float limit = strip.Max.X - inset;

        if (tile.Author is { Length: > 0 } author)
        {
            var style = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float width = Crystarium.MeasureText(author, style).X;
            if (x + width > limit)
                return;
            Crystarium.TextInBand(
                new Vector2(x, strip.Min.Y),
                new Vector2(width, strip.Size.Y),
                author,
                style);
            x += width + gap;
        }

        for (int i = 0; i < tile.Tags.Count; i++)
        {
            float width = TagChip(
                vm, tile.Tags[i], x, strip, limit, scale, theme);
            if (!(width > 0f))
                return;
            x += width + theme.Spacing.Two * scale;
        }
    }

    /// <summary>One filterable tag chip. Returns its width, or 0 when it did
    /// not fit and the strip must stop.</summary>
    private static float TagChip(
        PoseLibraryViewModel vm,
        string tag,
        float x,
        WindowFrameRect strip,
        float limit,
        float scale,
        Theme theme)
    {
        bool active = string.Equals(vm.ActiveTag, tag, StringComparison.Ordinal);
        var style = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = active ? theme.Accent : theme.FormLabel,
        };
        float text = Crystarium.MeasureText(tag, style).X;
        float padX = theme.Spacing.Three * scale;
        float width = text + padX * 2f;
        if (x + width > limit)
            return 0f;

        float height = theme.Controls.SwitchHeight * scale;
        var min = new Vector2(x, strip.Min.Y + (strip.Size.Y - height) * 0.5f);
        ImGui.SetCursorScreenPos(min);
        var hit = Interactive.Reserve(
            tag, new Vector2(width, height), disabled: false);
        ImGui.GetWindowDrawList().AddRectFilled(
            min,
            min + new Vector2(width, height),
            Packed(active
                ? theme.Chrome.AccentFill
                : hit.Hovered
                    ? theme.Chrome.ControlHover
                    : theme.Chrome.ControlFill),
            theme.Radii.Pill * scale);
        Crystarium.TextInBand(
            new Vector2(min.X + padX, min.Y),
            new Vector2(text, height),
            tag,
            style);

        if (hit.Clicked)
            vm.OnTagFilter?.Invoke(active ? null : tag);
        return width;
    }

    // ---- Footer -----------------------------------------------------

    // ---- Context menu -----------------------------------------------

    /// <summary>
    /// The tile menu, pumped OUTSIDE the grid's scroll child so the floating
    /// surface hosts itself at the top level. The rows are rewritten into the
    /// model's own array at open, so the cold path allocates nothing either.
    /// </summary>
    private static void DrawMenu(PoseLibraryViewModel vm)
    {
        if (vm.MenuRequested)
        {
            // The request is consumed whether or not it can still be served:
            // a stale target must not re-open the menu every frame.
            vm.MenuRequested = false;
            if (vm.MenuTile >= 0 && vm.MenuTile < vm.Tiles.Count)
            {
                var tile = vm.Tiles[vm.MenuTile];
                vm.MenuItems[0] = new ContextMenuItem(
                    "Apply", TablerIcon.Check, disabled: !vm.CanApply);
                vm.MenuItems[1] = new ContextMenuItem(
                    "Spawn as new actor",
                    TablerIcon.UserPlus,
                    disabled: !vm.CanSpawn);
                vm.MenuItems[2] = new ContextMenuItem(
                    tile.Favorite ? "Unfavorite" : "Favorite",
                    TablerIcon.Star);
                Crystarium.FloatingMenu.Open(
                    MenuId, ImGui.GetMousePos(), vm.MenuItems);
            }
        }

        int clicked = Crystarium.FloatingMenu.Draw(MenuId);
        if (clicked < 0 || vm.MenuTile < 0 || vm.MenuTile >= vm.Tiles.Count)
            return;
        switch (clicked)
        {
            case 0:
                vm.OnApplyTile?.Invoke(vm.MenuTile);
                break;
            case 1:
                vm.OnSpawnTile?.Invoke(vm.MenuTile);
                break;
            case 2:
                vm.OnToggleFavorite?.Invoke(vm.MenuTile);
                break;
        }
    }

    // ---- Shared -----------------------------------------------------

    /// <summary>Band-centred text, constrained ONLY on overflow: the truncate
    /// clip's snapped edge shaves a fitting run's descender otherwise. The fit
    /// lives in the row's own slot, because resolving it re-shapes the run and
    /// allocates while the grid restates every caption every frame.
    /// </summary>
    private static void Fitted(
        ref TextFit fit,
        Vector2 min,
        Vector2 band,
        string text,
        in TextStyle style,
        TextAlign align = TextAlign.Start,
        bool besideIcon = true)
    {
        if (!(band.X > 0f))
            return;
        if (fit.Resolve(text, style, band.X) is not { } fitted)
            Crystarium.TextInBand(min, band, text, style, align, besideIcon);
        else
            // The constraint box IS the band, so the run's own alignment
            // inside it is what carries the caller's intent. The fitted run
            // goes back through that same constraint rather than being drawn
            // plainly: the CLIP is what makes an unfittable run correct, and
            // it is what places the run to the pixel. Re-checking a run that
            // already fits costs the renderer no allocation.
            Crystarium.TextInBand(
                min, band, fitted, style,
                TextConstraint.Truncate(band.X, align),
                TextAlign.Start, besideIcon);
    }

    private static uint Packed(Vector4 color) =>
        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
}
