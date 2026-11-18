using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Widgets;
using Poser.Domain.Library;
using static Poser.UI.Widgets.ActionBarWidgets;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.DropdownWidgets;
using static Poser.UI.Widgets.FilterPillWidgets;
using static Poser.UI.Widgets.ScrollRegionWidgets;
using static Poser.UI.Widgets.SliderWidgets;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Views;

/// <summary>
/// A thumbnail ready to draw: the texture and the image's own size in pixels.
/// The tile is square and the image usually is not, so fitting it needs both;
/// the lookup gives both or nothing. Shared textures must be looked up again
/// every frame, so this is asked for each time rather than stored.
/// </summary>
public readonly record struct PoseThumbnail(nint Texture, Vector2 Size)
{
    public bool Ready => Texture != 0 && Size.X > 0f && Size.Y > 0f;
}

/// <summary>
/// Remembers how one caption was shortened to fit. Shortening text with "…"
/// measures it again and builds a new string, and grid captions are where text
/// is cut most often, so it is worked out once and reused every frame while the
/// text and width stay the same. Rows are reused when the filter changes, so the
/// text itself is part of what is compared.
/// </summary>
internal struct TextFit
{
    private string? _source;
    private float _band;
    private float _size;
    private string? _fitted;

    /// <summary>Whether the text had to be cut the last time.</summary>
    internal readonly bool Truncated => _fitted is not null;

    /// <summary>The text cut to fit <paramref name="band"/> wide, or null when it
    /// fits as it is. The font size is part of what is compared, so a theme that
    /// changes text size without changing the width still refits. A style that
    /// leaves its size to the theme must not be remembered here.
    /// </summary>
    internal string? Resolve(string text, in TextStyle style, float band)
    {
        float size = style.Size ?? 0f;
        if (!ReferenceEquals(_source, text) || _band != band || _size != size)
        {
            _source = text;
            _band = band;
            _size = size;
            _fitted = FitTruncated(text, style, band);
        }
        return _fitted;
    }
}

/// <summary>
/// One row in the folder list: a source, a subfolder, or a row such as All or
/// Favorites. Labels and counts are formatted when the list is rebuilt.
/// </summary>
public sealed class PoseLibraryFolderRow
{
    public required string Key;
    public required string Label;
    public required string LabelLower;
    public required int Depth;
    public int Count;
    /// <summary>The count text, built in advance, or null to show none.</summary>
    public string? CountText;
    internal TextFit LabelFit;
}

/// <summary>
/// One tile. <see cref="Id"/> is its ImGui id (the full path, so it stays the
/// same when the grid is filtered), <see cref="ThumbKey"/> is what the thumbnail
/// lookup is asked for, and every text it shows (label, modified time, tags) is
/// built with the row.
/// </summary>
public sealed class PoseLibraryTileRow
{
    public required string Id;
    public required string Label;
    public required string LabelLower;
    /// <summary>The formatted modification time, or a summary of the scene contents.
    /// </summary>
    public required string Sub;
    public string Modified = string.Empty;
    public Poser.Documents.Files.SceneMetadataReadOutcome? SceneMetadata;
    public required string ThumbKey;
    public bool HasThumbnail;
    public bool Favorite;
    /// <summary>The icon the tile shows when there is no thumbnail: a skeleton
    /// for a pose, a plain file for an Anamnesis <c>.cmp</c>, a person for a
    /// character file.</summary>
    public TablerIcon Fallback = TablerIcon.Armature;
    public string? Author;
    /// <summary>Whether the file behind the tile has been read: author,
    /// tags, status and a scene's contents arrive on selection.</summary>
    public bool Enriched;
    /// <summary>The tags shown in the info strip. Never null; usually empty.
    /// </summary>
    public IReadOnlyList<string> Tags = Array.Empty<string>();
    /// <summary>Index into <see cref="PoseLibraryViewModel.Folders"/>.
    /// </summary>
    public int Folder;

    /// <summary>The grid section this tile belongs to, for a tab that groups by
    /// something other than its folder: scenes group by where and when they were
    /// taken, which no folder records. Empty means the folder is the section, as
    /// on every other tab. Tiles with a key must arrive sorted by it, because the
    /// grid starts a new section whenever the key changes; unsorted tiles would
    /// split into repeated sections.
    /// </summary>
    public string SectionKey = string.Empty;
    public string SectionLabel = string.Empty;

    /// <summary>Whether reading the file's details failed: it is damaged, from a
    /// newer Poser, or too large. The tile shows a warning badge and the info
    /// strip shows <see cref="StatusText"/> instead of author and tags. The tile
    /// can still be seen and selected.</summary>
    public bool Flagged;

    /// <summary>The status line for a flagged tile ("Unreadable: …"), built in
    /// advance. Empty on a healthy tile.</summary>
    public string StatusText = string.Empty;

    internal TextFit LabelFit;
    internal TextFit SubFit;
    internal TextFit StatusFit;
}

/// <summary>
/// One section of the grid that can be folded away: a folder, or one auto-save
/// snapshot. The pane builds the label and count when the filter changes, and
/// says which run of <see cref="PoseLibraryViewModel.Visible"/> the section
/// covers. The grid never groups anything itself.
/// </summary>
public sealed class PoseLibraryGroupRow
{
    /// <summary>Stays the same when the filter changes. It is both what the
    /// folded state is remembered by and the header's ImGui id.</summary>
    public string Key = string.Empty;
    public string Label = string.Empty;
    /// <summary>The count text, built in advance.</summary>
    public string CountText = string.Empty;
    public bool Collapsed;
    /// <summary>First index into <see cref="PoseLibraryViewModel.Visible"/>.
    /// </summary>
    public int Start;
    /// <summary>Visible tiles in the section. Never 0: an empty section is not
    /// listed at all.</summary>
    public int Count;
    internal TextFit LabelFit;
}

/// <summary>
/// One horizontal strip of the grid: a section header, or one row of tiles.
/// Headers and tile rows have different heights, so the grid keeps this list.
/// It is rebuilt only when the visible tiles, the number of columns or the tile
/// size changes, never every frame.
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
    /// header; -1 for a row of tiles.</summary>
    internal readonly int Group;

    /// <summary>First index into <see cref="PoseLibraryViewModel.Visible"/>;
    /// unused by a header.</summary>
    internal readonly int Start;

    internal readonly int Count;

    /// <summary>Distance from the top of the grid's content, before UI scaling.</summary>
    internal readonly float Top;

    internal readonly float Height;

    internal float Bottom => Top + Height;
}

public sealed class PoseLibraryViewModel
{
    /// <summary>Rows such as All and Favorites first (RailHeads gives how many), then sources and folders.</summary>
    public List<PoseLibraryFolderRow> Folders = [];

    public int SelectedFolder;

    public List<PoseLibraryTileRow> Tiles = [];

    /// <summary>Indices into <see cref="Tiles"/> that match the search and the
    /// chosen folder. Refilled when either changes, and reused on every other
    /// frame.
    /// </summary>
    public List<int> Visible = [];

    /// <summary>The grid's sections, in <see cref="Visible"/> order, each
    /// covering one unbroken run of it. Read only while <see cref="Grouped"/>.</summary>
    public List<PoseLibraryGroupRow> Groups = [];

    /// <summary>Whether the grid draws section headers at all. A folder with no
    /// subfolders is one section, and a single header would only repeat the
    /// folder list, so then the grid has no headers.</summary>
    public bool Grouped;

    /// <summary>Raised by the pane whenever <see cref="Visible"/>,
    /// <see cref="Groups"/> or a folded state changes. The grid rebuilds its
    /// strips when this changes, instead of comparing everything every frame.</summary>
    public int LayoutRevision;

    /// <summary>Whether the folder list is shown at all.</summary>
    public bool ShowRail = true;

    /// <summary>
    /// How many fixed rows the folder list starts with before the real folders:
    /// two on most tabs ("All …" then "Favorites"), and none on the auto-saves
    /// tab, which has no favourites row. Everything that depends on row
    /// positions (the star icon here, and the folder range and favourites
    /// filter in the pane) reads this value rather than a fixed number, so the
    /// tabs cannot get out of step.
    /// </summary>
    public int RailHeads = 2;

    /// <summary>Whether to show the "no pose sources" message instead of the
    /// grid.</summary>
    public bool ShowNoSources;

    /// <summary>The message the pane builds when no source is set up, or every
    /// source failed to load.</summary>
    public string NoSourcesTitle = "No pose sources found.";
    public string NoSourcesDetail = string.Empty;

    /// <summary>The text an empty grid shows, set by the pane.</summary>
    public string EmptyText = "No matches.";

    /// <summary>Index into <see cref="Tiles"/>, not into
    /// <see cref="Visible"/>; -1 is no selection.</summary>
    public int Selected = -1;

    public string Query = string.Empty;

    public LibraryBrowseSort Sort;
    public bool ShowSort = true;
    public Action<int>? OnSort;

    /// <summary>The tag being filtered on, shown as a chip that can be removed.
    /// </summary>
    public string? ActiveTag;

    /// <summary>The tile size before UI scaling; the view keeps it between 80 and 200.</summary>
    public float IconSize = 120f;

    public bool IsScanning;

    /// <summary>The footer text, set by the pane.</summary>
    public string Status = string.Empty;

    /// <summary>The main button's label; the pane sets "Apply to {name}".</summary>
    public string ApplyLabel = "Apply";
    /// <summary>Whether the main button resets the animation, as applying a
    /// character file does, and so uses the Disruptive colour.</summary>
    public bool ApplyDisruptive;

    public bool CanApply;
    public bool CanSpawn;

    /// <summary>Whether a character file being applied is still working.
    /// Applying an MCDF takes a while and is started from this pane, so its
    /// stop button belongs next to the button that started it, not only on the
    /// appearance pane's progress row. Set by the pane, and only ever true on
    /// the tab that can start one.</summary>
    public bool ShowCancelImport;

    /// <summary>Whether that stop button still works. Once the apply is past the
    /// point where cancelling means anything, the button greys out instead of
    /// vanishing, as on the appearance pane's progress row. A button that
    /// disappears partway through looks like a bug.</summary>
    public bool CanCancelImport;

    /// <summary>Whether the footer shows "Spawn as new" at all. A scene is
    /// loaded whole, never spawned as an actor, so the Scenes tab does not show
    /// the button at all rather than showing it greyed out.</summary>
    public bool ShowSpawn = true;

    /// <summary>The placement choice beside the main button, on the tabs that
    /// have one (Objects and Scenes). Null hides it.</summary>
    public string[]? PlacementOptions;
    public int PlacementSelected;
    public Action<int>? OnPlacement;

    /// <summary>Whether the footer shows the button for editing a file's
    /// details, only on the tab whose files have an author and tags. A
    /// right-click menu alone is not a place anyone finds a feature.</summary>
    public bool ShowEditMetadata;

    /// <summary>Whether the selected file can be edited: it reads correctly and
    /// is not in an old format, the same check the right-click menu uses.</summary>
    public bool CanEditMetadata;

    /// <summary>Whether the footer shows the save button. The library is where
    /// scenes are found, so saving the current one belongs next to them rather
    /// than only behind a menu.</summary>
    public bool ShowSaveScene;

    /// <summary>Whether a scene save or load is running right now. This save
    /// button is the same one the scene workspace has, so both are disabled
    /// the same way.</summary>
    public bool SceneBusy;

    /// <summary>Whether the action row shows the two menu buttons, import
    /// options and the bone filter. Only the Poses tab does.</summary>
    public bool ShowImportMenus;
    public Action? OnImportMenu;
    public Action? OnBoneFilterMenu;

    /// <summary>Whether this tab can preview at all; set by the pane. On tabs
    /// that can, the preview is always on. MCDF tiles are never imported as a
    /// pose, so they cannot be previewed.</summary>
    public bool PreviewAvailable;

    /// <summary>Looks up a tile's thumbnail. Called for every visible tile every
    /// frame: shared textures must be looked up again each time, so this can
    /// never give back a stored one. An empty answer draws the fallback icon.
    /// </summary>
    public Func<string, PoseThumbnail>? ResolveThumbnail;

    public Action<string>? OnQuery;
    public Action<int>? OnSelectFolder;

    /// <summary>A section header's fold arrow was clicked; receives the index
    /// into <see cref="Groups"/>.</summary>
    public Action<int>? OnToggleGroup;

    /// <summary>A single click on a tile; receives its index into
    /// <see cref="Tiles"/>.</summary>
    public Action<int>? OnSelect;
    /// <summary>A click with its modifier keys: Ctrl adds or removes the tile,
    /// Shift selects the range from the main selected tile, and a plain click
    /// selects only that tile.</summary>
    public Action<int, bool, bool>? OnSelectWith;
    /// <summary>The tiles inside a drag-selection box, and whether Ctrl was held to add them.</summary>
    public Action<IReadOnlyList<int>, bool>? OnMarquee;
    /// <summary>Every selected tile; <see cref="Selected"/> is the main one.
    /// Actions on several files act on all of them.</summary>
    internal readonly HashSet<int> SelectedSet = new();
    /// <summary>The tiles drawn this frame and where they were drawn, which a
    /// drag-selection box is checked against.</summary>
    internal readonly List<(int Index, Vector2 Min, Vector2 Max)> TileRects = new();
    internal Vector2? MarqueeStart;
    /// <summary>The grid's visible height before UI scaling; the empty text is
    /// centred in it.</summary>
    internal float GridHeight;
    /// <summary>The actor the footer's Apply goes to, for poses and character
    /// files: a dropdown beside the button, set to the selected actor by default.</summary>
    internal bool ShowApplyTarget;
    internal string[] ApplyTargetNames = [];
    internal int ApplyTargetIndex = -1;
    internal Action<int>? OnApplyTarget;

    /// <summary>Applies a tile: from a double click, the footer's main button
    /// or the right-click menu.</summary>
    public Action<int>? OnApplyTile;

    public Action<int>? OnSpawnTile;
    public Action<int>? OnToggleFavorite;

    /// <summary>The footer's main button: opens the list of actors to apply to.</summary>
    public Action? OnApplyMenu;

    /// <summary>Stops the character-file apply this pane started.</summary>
    public Action? OnCancelImport;

    /// <summary>The footer's save button, on the Scenes tab.</summary>
    public Action? OnSaveScene;

    /// <summary>The footer's button for editing the selected file's details.
    /// </summary>
    public Action<int>? OnEditMetadata;

    /// <summary>Whether this tab allows changing a tile's favorite state.</summary>
    public bool CanFavorite = true;

    /// <summary>A tag chip was clicked; null clears the filter.</summary>
    public Action<string?>? OnTagFilter;

    public Action<float>? OnIconSize;
    public Action? OnRefresh;
    public Action? OnOpenSettings;

    // Made once per model, so drawing the window does not create new delegates
    // every frame. Each one uses nothing but this model.
    internal Action<ActionBarScope>? Footer;
    internal Action<ActionBarScope>? FooterActions;
    internal Action<ScrollRegionScope>? Rail;
    internal Action<ScrollRegionScope>? Grid;
    internal Action? SpawnClick;
    internal Action? ApplyClick;
    internal Action? SettingsClick;
    internal Action<float>? IconSizeChange;
    internal Action? ImportMenuClick;
    internal Action? BoneFilterClick;
    internal Action? ApplyMenuClick;
    internal Action? SaveSceneClick;
    internal Action? EditMetadataClick;

    // The grid's strips and the row map the clipper uses, kept on the model
    // because the view itself is static. Rebuilt only when one of the three
    // Built values below no longer matches.
    internal readonly List<GridBand> Bands = [];
    internal readonly List<int> Slots = [];
    internal float BandsHeight;
    internal int SlotCount;
    internal int BuiltRevision = -1;
    internal int BuiltColumns = -1;
    internal float BuiltPitch = -1f;

    // The tile the right-click menu is for. The pane opens and draws the menu,
    // because its rows depend on the tab, whether the file reads correctly,
    // and which recovery actions apply; the view only reports the right-click.
    internal int MenuTile = -1;
    internal bool MenuRequested;
}

/// <summary>
/// Draws the library's search, folder list, grid, info strip and buttons inside
/// the area its window gives it.
///
/// <para>The folder list is small, so it is always drawn in full. The tile grid
/// can hold thousands of files, so only the rows on screen are drawn: it uses
/// <see cref="ImGuiListClipper"/> over rows of equal height, with a loop over
/// the columns of each row.</para>
/// </summary>
public static class PoseLibraryView
{
    private const string SearchId = "##pose-library-search";
    private const string SortId = "##pose-library-sort";
    private const string RailId = "##pose-library-folders";
    private const string GridId = "##pose-library-grid";
    private const string RefreshId = "##pose-library-refresh";
    private const string ActiveTagId = "##pose-library-active-tag";
    private const string SliderId = "##pose-library-icon-size";
    private const string SettingsId = "##pose-library-open-settings";
    private const string ActionRowId = "pose-library-actions";

    // Ids inside each tile. They can be constants because every tile first
    // pushes its own path as an ImGui id, so they are unique per tile without
    // building a new string for every tile every frame.
    private const string TileId = "##tile";
    private const string StarId = "##star";
    private const string TileLabelHelpId = "##tile-label";
    private const string FolderLabelHelpId = "##folder-label";

    /// <summary>The height of the top bar under the title bar, which is also the
    /// search field's own height.</summary>
    private const float BandHeight = 36f;

    /// <summary>The two lines of text under a tile's thumbnail: the name, then
    /// the modified time.</summary>
    private const float CaptionHeight = 34f;

    private const float LabelLineHeight = 18f;

    private const float SubLineHeight = CaptionHeight - LabelLineHeight;

    /// <summary>The strip shown under the grid while a tile is selected. The
    /// grid's scroll area gets shorter by exactly this much.</summary>
    private const float InfoStripHeight = 28f;

    /// <summary>How far the top bar, tabs and footer sit in from the pane's
    /// edge: half the usual page inset. The workspace already has its own frame,
    /// so a full inset on top would be doubled padding. The grid and the folder
    /// list use their own insets on purpose.</summary>
    private const float PaneInset = 6f;

    private const float MinimumIconSize = 80f;

    private const float MaximumIconSize = 200f;

    private const float SliderWidth = 120f;
    private const float SortWidth = 110f;

    private static readonly string[] SortOptions = ["Name", "Newest", "Oldest"];

    /// <summary>The grid's scroll bar is half the usual gutter wide, and the
    /// first column keeps the same space from the left edge, as in the spawn
    /// browser.</summary>
    private const float GridBarShare = 0.5f;

    /// <summary>Space between the grid and the top bar above and the strip below.
    /// </summary>
    private const float GridVPad = 4f;

    /// <summary>The icon space in a folder row: a 2px left margin, then a
    /// square as tall as the row with the small icon centred in it.</summary>
    private const float NavigationIconMargin = 2f;

    private const float NavigationPillRadius = 5f;

    /// <summary>The indent for one level of subfolder in the folder list.</summary>
    private const float FolderIndent = 12f;

    /// <summary>The fold arrow in a section header: a square before the title,
    /// sized like the icon space in a folder row.</summary>
    private const float HeaderChevronMargin = 2f;

    private const float HeaderPillRadius = 5f;

    /// <summary>The search field's own left padding; the top bar's margin adds
    /// the rest.</summary>
    private const float SearchInnerPad = 10f;

    private static readonly Action<string> IgnoreQuery = static _ => { };
    private static readonly Action<int> IgnoreSort = static _ => { };

    /// <summary>Draws the Library within the current pane rectangle.</summary>
    public static void Draw(PoseLibraryViewModel vm, Vector2 origin, Vector2 size)
    {
        if (!(size.X > 0f) || !(size.Y > 0f))
            return;
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;

        // Read Enter before the grid opens its scroll area, so what counts is
        // whether the library window has focus, not which inner area the
        // cursor is in. Key repeat is off, so holding Enter applies only once.
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
        vm.ImportMenuClick ??= () => vm.OnImportMenu?.Invoke();
        vm.BoneFilterClick ??= () => vm.OnBoneFilterMenu?.Invoke();
        vm.ApplyMenuClick ??= () => vm.OnApplyMenu?.Invoke();
        vm.SaveSceneClick ??= () => vm.OnSaveScene?.Invoke();
        vm.EditMetadataClick ??= () => vm.OnEditMetadata?.Invoke(vm.Selected);

        var rects = Bands(
            vm, origin, size, scale, theme);
        DrawBand(vm, rects.Band, scale, theme);
        DrawRail(vm, rects.Rail, scale, theme);
        DrawBody(vm, rects.Body, scale, theme);
        DrawActionRow(vm, rects.Footer, scale, theme);

        if (submit && vm.CanApply && vm.Selected >= 0
            && vm.Selected < vm.Tiles.Count)
            vm.OnApplyTile?.Invoke(vm.Selected);
    }

    /// <summary>Lays out the toolbar, folders, grid and footer within the pane.</summary>
    private static WindowFrameRects Bands(
        PoseLibraryViewModel vm,
        Vector2 origin,
        Vector2 size,
        float scale,
        Theme theme)
    {
        var max = origin + size;
        float rule = MathF.Max(1f, scale);
        float bandBottom = MathF.Min(max.Y, origin.Y + BandHeight * scale);
        float rowTop = MathF.Max(
            bandBottom, max.Y - theme.Floating.ModalBarHeight * scale);
        // The folder list never takes more than half the pane, so a narrow
        // workspace still shows a grid rather than only folders.
        float railWidth = vm.ShowRail
            ? MathF.Min(theme.Settings.NavigationWidth * scale, size.X * 0.5f)
            : 0f;

        var draw = ImGui.GetWindowDrawList();
        uint separator = Packed(theme.FormSeparator);
        draw.AddRectFilled(
            new Vector2(origin.X, bandBottom - rule),
            new Vector2(max.X, bandBottom),
            separator);
        draw.AddRectFilled(
            new Vector2(origin.X, rowTop),
            new Vector2(max.X, rowTop + rule),
            separator);

        var rail = new WindowFrameRect(
            new Vector2(origin.X, bandBottom),
            new Vector2(origin.X + railWidth - rule, rowTop));
        if (railWidth > rule)
        {
            // Filled like the surfaces around it: the raised colour at the
            // window opacity from Settings, never fully solid.
            draw.AddRectFilled(
                rail.Min, rail.Max, Packed(theme.SurfaceRaised with
                {
                    W = FloatingSurface.FillColor.W,
                }));
            draw.AddRectFilled(
                new Vector2(rail.Max.X, bandBottom),
                new Vector2(rail.Max.X + rule, rowTop),
                separator);
        }

        return new WindowFrameRects
        {
            Band = new WindowFrameRect(
                origin, new Vector2(max.X, bandBottom - rule)),
            Rail = rail,
            Body = new WindowFrameRect(
                new Vector2(origin.X + railWidth, bandBottom),
                new Vector2(max.X, rowTop)),
            Footer = new WindowFrameRect(
                new Vector2(origin.X, rowTop + rule),
                new Vector2(max.X, max.Y)),
        };
    }

    /// <summary>Draws status on the left and the current tab's actions on the right.</summary>
    private static void DrawActionRow(
        PoseLibraryViewModel vm, WindowFrameRect footer, float scale, Theme theme)
    {
        if (!(footer.Size.X > 0f) || !(footer.Size.Y > 0f))
            return;
        float inset = PaneInset * scale;
        ActionBar(
            ActionRowId,
            new Vector2(footer.Min.X + inset, footer.Min.Y),
            new Vector2(
                MathF.Max(0f, footer.Size.X - inset * 2f), footer.Size.Y),
            vm.Footer!,
            vm.FooterActions,
            ActionBarSeparator.None);
    }

    private static void Actions(
        PoseLibraryViewModel vm, ActionBarScope scope)
    {
        // The menus come first in the one bottom row. The position, rotation
        // and scale switches are not here: the inspector decides which of them
        // a pose applies, and a second set here would make this tab's footer
        // taller than every other tab's.
        if (vm.ShowImportMenus)
            scope.Button("Options", vm.ImportMenuClick!);
        // Applying a character file takes a while and starts from this pane,
        // so while it runs, its stop button comes first in the row: nobody has
        // to find another pane to get out of an import. The button stays in
        // place and greys out during steps that cannot be cancelled.
        if (vm.ShowCancelImport)
            scope.Button(
                "Cancel import",
                vm.OnCancelImport!,
                help: vm.CanCancelImport
                    ? "Stop applying this character file. Everything it has "
                        + "already applied is undone."
                    : "This phase cannot be cancelled",
                disabled: !vm.CanCancelImport);
        bool none = vm.Selected < 0 || vm.Selected >= vm.Tiles.Count;
        if (vm.ShowSpawn)
            scope.Button(
                "Spawn as new",
                vm.SpawnClick!,
                disabled: none || !vm.CanSpawn);
        // The scene workspace's own save button is disabled while a scene save
        // or load runs, so this one is too. The same save window must not open
        // from one place and not the other.
        if (vm.ShowSaveScene)
            scope.Button(
                "Save scene…",
                vm.SaveSceneClick!,
                disabled: vm.SceneBusy);
        // Editing author and tags, in the row people already look at. The
        // right-click menu has the same action, but this is the one people see.
        if (vm.ShowEditMetadata)
            scope.Button(
                "Edit metadata…",
                vm.EditMetadataClick!,
                disabled: none || !vm.CanEditMetadata);
        // The placement sits next to the button it changes: the label says
        // what the box is for, and the box says where the loaded entry goes.
        if (vm.PlacementOptions is { } placement)
        {
            scope.Label("Load at");
            scope.Dropdown(
                "placement", placement, vm.PlacementSelected,
                vm.OnPlacement!,
                help: "Where a spawned entry lands");
        }
        // Poses and character files apply to the actor named beside the
        // button: the selected actor by default, or any actor in the scene.
        if (vm.ShowApplyTarget)
        {
            scope.Label("Apply to");
            scope.Dropdown(
                "apply-target",
                vm.ApplyTargetNames,
                Math.Max(0, vm.ApplyTargetIndex),
                vm.OnApplyTarget!,
                help: "The actor the file applies to");
        }
        scope.Button(
            vm.ApplyLabel,
            vm.ApplyMenuClick!,
            disabled: none || !vm.CanApply,
            variant: vm.ApplyDisruptive ? ButtonVariant.Disruptive : ButtonVariant.Primary);
    }

    // ---- Top bar ----------------------------------------------------

    /// <summary>What is inside the top bar: the search field, the rescan button,
    /// sort, size and the tag chip. <see cref="Bands"/> draws the bar and its
    /// line. The controls on the right are placed first, so the search field
    /// takes exactly the space that is left.</summary>
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
        IconButton(
            TablerIcon.Refresh,
            vm.OnRefresh,
            style: ControlStyle.Square(action),
            disabled: vm.IsScanning,
            help: "Rescan source folders",
            id: RefreshId);
        right -= actionPx + gap;

        if (vm.ShowSort)
        {
            ImGui.SetCursorScreenPos(new Vector2(
                right - SortWidth * scale,
                band.Min.Y + (band.Size.Y - theme.Controls.WorkspaceHeight * scale) * 0.5f));
            Dropdown(
                SortId,
                SortOptions,
                (int)vm.Sort,
                vm.OnSort ?? IgnoreSort,
                ControlStyle.Workspace with { Width = UiWidth.Fixed(SortWidth) },
                help: "Sort library entries");
            right -= SortWidth * scale + gap;
        }

        // There is no preview switch: on tabs that can preview, the preview in
        // the inspector is always on. The size slider is in the top bar, not
        // the footer, because the footer's buttons did not fit beside it in a
        // narrow window.
        if (right - SliderWidth * scale
            > band.Min.X + band.Size.X * 0.5f)
        {
            float sliderHeight = theme.Controls.SliderHeight * scale;
            ImGui.SetCursorScreenPos(new Vector2(
                right - SliderWidth * scale,
                band.Min.Y + (band.Size.Y - sliderHeight) * 0.5f));
            Slider(
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

        // The margin tops up the search field's own padding, so the search
        // icon lines up with the folder list's icons. The tabs are in the
        // window's tab strip, not in this bar.
        float left = band.Min.X
            + MathF.Max(0f, inset - SearchInnerPad * scale);
        float width = (right - left) / scale;
        if (!(width > 0f))
            return;
        ImGui.SetCursorScreenPos(new Vector2(left, band.Min.Y));
        FilterPill(
            SearchId,
            vm.Query,
            vm.OnQuery ?? IgnoreQuery,
            "Search",
            new ControlStyle { Width = UiWidth.Fixed(width) });
    }

    /// <summary>The tag chip that clears the tag filter. Clicking anywhere on
    /// it clears the filter; the cross only shows that. Returns its left edge.</summary>
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
        float text = MeasureText(tag, style).X;
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
        TextInBand(
            new Vector2(min.X + padX, min.Y),
            new Vector2(text, height),
            tag,
            style,
            TextAlign.Start,
            besideIcon: true);
        var glyphMin = new Vector2(
            min.X + width - padX - glyph, min.Y + (height - glyph) * 0.5f);
        IconIn(
            glyphMin, glyphMin + new Vector2(glyph), TablerIcon.X, theme.Accent);

        if (hit.Hovered)
            HoverHelp.Explain(
                ActiveTagId, min, min + new Vector2(width, height),
                "Clear the tag filter");
        if (hit.Clicked)
            vm.OnTagFilter?.Invoke(null);
        return min.X;
    }

    // ---- Folder list ------------------------------------------------

    /// <summary>What is inside the folder list: <see cref="Bands"/> draws its
    /// background and line, this draws the inset and the rows. There are only a
    /// handful of folders, so every row is drawn; this is the only part of the
    /// view where that is true.</summary>
    private static void DrawRail(
        PoseLibraryViewModel vm, WindowFrameRect rail, float scale, Theme theme)
    {
        if (!(rail.Size.X > 0f))
            return;
        float inset = theme.Page.Inset;
        // The scroll area spans the full width of the folder list. The scroll
        // bar's space is the right-hand inset of the rows, not a second margin,
        // so only the top and bottom inset is applied here.
        ImGui.SetCursorScreenPos(
            new Vector2(rail.Min.X, rail.Min.Y + inset * scale));
        ScrollRegion(
            RailId,
            rail.Size.X / scale,
            rail.Size.Y / scale - inset * 2f,
            vm.Rail!);
    }

    private static void DrawFolders(
        PoseLibraryViewModel vm, ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float inset = theme.Page.Inset;
        float gutter = theme.Scrollbar.GutterWidth;
        // The scroll bar's space is the rows' right-hand inset, not a narrower
        // row: the row reaches under the scroll bar, while its highlight and
        // content stop before it. So the space on the right matches the left,
        // whether or not the scroll bar is shown.
        float width =
            MathF.Max(1f, region.ContentWidth + gutter - inset) * scale;
        var origin = ImGui.GetCursorScreenPos() + new Vector2(inset * scale, 0f);

        // The rows sit directly on top of each other. ImGui's usual space
        // between items is for the layout around the list, not for its rows.
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
                        // The fixed rows are always in the same place, so the
                        // Favorites row can get its star here without the pane
                        // giving an icon per row. Only a list that has a
                        // Favorites row has it at index 1.
                        i == 1 && vm.RailHeads > 1
                            ? TablerIcon.Star
                            : TablerIcon.Folder,
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

    /// <summary>One folder row, drawn by hand like the rows in the Settings
    /// side list: its highlight fills the whole row and its icon is fully
    /// opaque, which the shared tree row does not do.</summary>
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

        // The row reaches under the scroll bar; its highlight and everything
        // inside it stop here instead, as tree rows do.
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
        IconIn(glyphMin, glyphMin + new Vector2(glyph), icon);

        float labelX = slotMin.X + height;
        // The count keeps some space from the highlight's right edge, and the
        // label keeps some space from the count.
        float labelRight = contentRight - theme.Spacing.Four * scale;
        if (row.CountText is { Length: > 0 } count)
        {
            var countStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Family = FontFamily.Mono,
                Color = theme.FormLabel,
            };
            float countWidth = MeasureText(count, countStyle).X;
            labelRight -= countWidth;
            TextInBand(
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

        // As with tile captions, the fit already knows whether the name was
        // cut. A cut name shows in full on hover without measuring it again,
        // and a name that fits costs nothing.
        if (hit.Hovered && row.LabelFit.Truncated)
            HoverHelp.Preview(
                FolderLabelHelpId,
                hit.ScreenMin,
                new Vector2(contentRight, hit.ScreenMax.Y),
                row.Label);
        return hit.Activated;
    }

    // ---- Grid area --------------------------------------------------

    private static void DrawBody(
        PoseLibraryViewModel vm, WindowFrameRect body, float scale, Theme theme)
    {
        if (!(body.Size.X > 0f) || !(body.Size.Y > 0f))
            return;

        // A Library with no sources needs the setup prompt instead of an empty search result.
        if (vm.ShowNoSources)
        {
            DrawNoSources(vm, body, scale, theme);
            return;
        }

        // Reserve space for metadata only when the selected entry has something to show.
        bool selected = vm.Selected >= 0 && vm.Selected < vm.Tiles.Count
            && HasStripContent(vm.Tiles[vm.Selected]);
        float strip = selected ? InfoStripHeight : 0f;
        vm.GridHeight = body.Size.Y / scale - strip;
        ImGui.SetCursorScreenPos(body.Min);
        ScrollRegion(
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

    /// <summary>Explains how to add a Library source and provides the Settings button.</summary>
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
        float detail = vm.NoSourcesDetail.Length == 0 ? 0f
            : row + theme.Spacing.One * scale;
        float block = row + detail + theme.Spacing.Three * scale + button;
        float top = body.Min.Y + (body.Size.Y - block) * 0.5f;

        TextInBand(
            new Vector2(body.Min.X, top),
            new Vector2(body.Size.X, row),
            vm.NoSourcesTitle,
            style,
            TextAlign.Center);

        if (detail > 0f)
        {
            TextInBand(
                new Vector2(body.Min.X, top + row),
                new Vector2(body.Size.X, row),
                vm.NoSourcesDetail,
                style,
                TextAlign.Center);
        }

        float width = MeasureButton(
            "Open Settings",
            ControlStyle.Comfortable).X;
        ImGui.SetCursorScreenPos(new Vector2(
            body.Min.X + (body.Size.X - width) * 0.5f,
            top + row + detail + theme.Spacing.Three * scale));
        Button(
            "Open Settings",
            vm.OnOpenSettings,
            style: ControlStyle.Comfortable,
            id: SettingsId);
    }

    /// <summary>Draws the visible rows. The row map lets the clipper handle section headers and tile rows of different heights.</summary>
    private static void DrawGrid(
        PoseLibraryViewModel vm, ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float inset = theme.Scrollbar.GutterWidth * GridBarShare;
        float gap = theme.Spacing.Three;
        float icon = Math.Clamp(
            vm.IconSize, MinimumIconSize, MaximumIconSize);
        float pitchX = icon + gap;
        float pitchY = icon + CaptionHeight + gap;
        float usable = MathF.Max(0f, region.ContentWidth - inset);
        // The last column has no gap after it, so one gap is added to the width
        // before dividing, rather than taking it off every column.
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
                EmptyLine(vm.EmptyText, region.ContentWidth, vm.GridHeight, scale, theme);
            }
            else
            {
                var origin = ImGui.GetCursorScreenPos();
                // A header reaches from the first tile column to under the scroll
                // bar; its own content stops before the scroll bar.
                float width = MathF.Max(1f, usable + inset) * scale;
                // Dragging on empty grid space starts a selection box; tiles drawn on top handle their own clicks.
                vm.TileRects.Clear();
                ImGui.InvisibleButton(
                    "##grid-ground",
                    new Vector2(width, MathF.Max(1f, vm.BandsHeight * scale)));
                ImGui.SetItemAllowOverlap();
                if (ImGui.IsItemActivated())
                    vm.MarqueeStart = ImGui.GetMousePos();
                ImGui.SetCursorScreenPos(origin);
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
                DrawMarquee(vm, theme);

                // The clipper stops at its last whole row, so this sets the
                // scroll height to the real height of the content.
                ImGui.SetCursorScreenPos(
                    origin + new Vector2(0f, vm.BandsHeight * scale));
                ImGui.Dummy(Vector2.Zero);
            }
            // Include the bottom padding in the scrollable height.
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

    /// <summary>Draws a section header that can be folded, with its tile count.</summary>
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
        IconIn(
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
        float countWidth = MeasureText(group.CountText, countStyle).X;
        labelRight -= countWidth;
        TextInBand(
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

    /// <summary>Rebuilds the strips when the tiles, the number of columns or the tile height change.</summary>
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
        // The clipper's last row also covers any leftover height at the end.
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

    /// <summary>Draws one tile, and handles selecting, starring and applying it.</summary>
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
        vm.TileRects.Add((index, min, min + size));

        // The tile's path is pushed as an ImGui id, so the two ids below are
        // unique per tile without building a new string for each tile.
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
            bool selected = vm.Selected == index || vm.SelectedSet.Contains(index);

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

            if (tile.Flagged)
            {
                // The warning badge goes top-left, since the star is top-right.
                // It shows on the tile that the file is damaged; the info strip
                // gives the full reason when the tile is selected.
                var flagMin = new Vector2(min.X + pad, min.Y + pad);
                IconIn(
                    flagMin,
                    flagMin + new Vector2(star),
                    TablerIcon.AlertTriangle,
                    theme.Warning);
            }

            if (tile.Favorite && vm.CanFavorite)
            {
                // A favourite's star shows even when the tile is not hovered.
                IconIn(
                    starMin, starMax, "star-filled", theme.Warning);
            }
            else if (hovered && vm.CanFavorite)
                IconIn(
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
            {
                var io = ImGui.GetIO();
                if (vm.OnSelectWith != null)
                    vm.OnSelectWith(index, io.KeyCtrl, io.KeyShift);
                else
                    vm.OnSelect?.Invoke(index);
            }
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

    /// <summary>Fits the thumbnail into the tile without cropping, or draws the tile's fallback icon.</summary>
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

        // The icon size is rounded down to steps of 8 pixels. The icon cache
        // draws each new pixel size once, slowly, so dragging the size slider
        // would draw a new icon every frame and stall the game. Within one
        // step the size repeats and the cached icon is reused.
        float bucket = 8f * scale;
        float side = MathF.Max(
            bucket,
            MathF.Floor(MathF.Min(box.X, box.Y) * 0.4f / bucket) * bucket);
        var glyphMin = theme.Optical.Snap(
            boxMin + (box - new Vector2(side)) * 0.5f);
        IconIn(
            glyphMin, glyphMin + new Vector2(side), tile.Fallback, theme.TextDim);
    }

    /// <summary>The tile's name and the line under it. The full name shows on hover only when it was
    /// cut; otherwise the hover would repeat what can already be read.</summary>
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

        // The fit already knows whether the name was cut, so the hover uses
        // that instead of measuring again.
        if (hovered && tile.LabelFit.Truncated)
            HoverHelp.Preview(
                TileLabelHelpId, min, min + size, tile.Label);
    }

    /// <summary>The grid's drag-selection box, from where the mouse was pressed
    /// on empty space to where it is now. On release, the tiles it touches are
    /// selected (Ctrl adds them). A box that touches nothing changes nothing.</summary>
    private static void DrawMarquee(PoseLibraryViewModel vm, Theme theme)
    {
        if (vm.MarqueeStart is not { } start)
            return;
        var mouse = ImGui.GetMousePos();
        var rmin = Vector2.Min(start, mouse);
        var rmax = Vector2.Max(start, mouse);
        bool isDrag = (rmax - rmin).LengthSquared() > 16f;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (isDrag)
            {
                var fg = ImGui.GetForegroundDrawList();
                fg.AddRectFilled(rmin, rmax, Packed(theme.Chrome.AccentFill));
                fg.AddRect(rmin, rmax, Packed(theme.AccentHover));
            }
            return;
        }
        if (isDrag)
        {
            var caught = new List<int>();
            foreach (var (index, min, max) in vm.TileRects)
                if (min.X <= rmax.X && max.X >= rmin.X
                    && min.Y <= rmax.Y && max.Y >= rmin.Y)
                    caught.Add(index);
            if (caught.Count > 0)
                vm.OnMarquee?.Invoke(caught, ImGui.GetIO().KeyCtrl);
        }
        vm.MarqueeStart = null;
    }

    private static void EmptyLine(
        string text, float contentWidth, float gridHeight, float scale, Theme theme)
    {
        float height = theme.Controls.ListRowHeight * scale;
        // Centred in the grid's whole height, the same on every tab.
        float above = MathF.Max(0f, (gridHeight * scale - height) * 0.5f - GridVPad * scale);
        if (above > 0f)
            ImGui.Dummy(new Vector2(0f, above));
        var min = ImGui.GetCursorScreenPos();
        TextInBand(
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

    /// <summary>Whether the info strip would show anything for this tile: a
    /// problem, an author, or a tag. These are exactly the three things
    /// <see cref="DrawInfoStrip"/> draws, so the space kept for the strip and
    /// what it shows always agree.</summary>
    private static bool HasStripContent(PoseLibraryTileRow tile) =>
        (tile.Flagged && tile.StatusText.Length > 0)
        || tile.Author is { Length: > 0 }
        || tile.Tags.Count > 0;

    /// <summary>
    /// The selected file's details, shown under the grid: the author, then its
    /// tags as chips that filter the grid. Each piece of text is drawn on its
    /// own; author and tags are never joined, so no frame builds a new string.
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

        if (tile.Flagged && tile.StatusText.Length > 0)
        {
            // For a damaged file the strip says what is wrong, in the badge's
            // colour, instead of an author and tags that could not be read.
            Fitted(
                ref tile.StatusFit,
                new Vector2(x, strip.Min.Y),
                new Vector2(limit - x, strip.Size.Y),
                tile.StatusText,
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.Warning,
                });
            return;
        }

        if (tile.Author is { Length: > 0 } author)
        {
            var style = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float width = MeasureText(author, style).X;
            if (x + width > limit)
                return;
            TextInBand(
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

    /// <summary>One tag chip that filters the grid. Returns its width, or 0 when
    /// it did not fit and the strip must stop.</summary>
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
        float text = MeasureText(tag, style).X;
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
        TextInBand(
            new Vector2(min.X + padX, min.Y),
            new Vector2(text, height),
            tag,
            style);

        if (hit.Clicked)
            vm.OnTagFilter?.Invoke(active ? null : tag);
        return width;
    }

    // ---- Shared -----------------------------------------------------

    /// <summary>Text centred in its space, and cut only when it does not fit:
    /// cutting text that fits would clip the bottom of letters like g and y.
    /// The fit is kept on the row, because working it out measures the text
    /// again and builds a string, and the grid draws every caption every frame.
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
            TextInBand(min, band, text, style, align, besideIcon);
        else
            // The cut text is drawn through the same width limit rather than
            // plainly. The limit is what keeps text that cannot fit inside its
            // space, and what places it exactly, with the caller's alignment.
            // Checking text that already fits creates nothing new.
            TextInBand(
                min, band, fitted, style,
                TextConstraint.Truncate(band.X, align),
                TextAlign.Start, besideIcon);
    }

    private static uint Packed(Vector4 color) =>
        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
}
