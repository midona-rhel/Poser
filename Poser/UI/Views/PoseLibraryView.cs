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
    /// <summary>An Anamnesis <c>.cmp</c>, which takes its own fallback glyph.
    /// </summary>
    public bool Legacy;
    public string? Author;
    /// <summary>The info strip's chips. Never null; empty is the norm.
    /// </summary>
    public IReadOnlyList<string> Tags = Array.Empty<string>();
    /// <summary>Index into <see cref="PoseLibraryViewModel.Folders"/>.
    /// </summary>
    public int Folder;
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

    /// <summary>Resolves a tile's thumbnail. Called per visible tile per
    /// frame: shared texture wraps must be re-resolved, so this can never
    /// answer with a stored handle. A default answer draws the fallback glyph.
    /// </summary>
    public Func<string, PoseThumbnail>? ResolveThumbnail;

    public Action<string>? OnQuery;
    public Action<int>? OnSelectFolder;

    /// <summary>Told an index into <see cref="Tiles"/>: a single click.
    /// </summary>
    public Action<int>? OnSelect;

    /// <summary>Double click, the footer's primary, and the context menu.
    /// </summary>
    public Action<int>? OnApplyTile;

    public Action<int>? OnSpawnTile;
    public Action<int>? OnToggleFavorite;

    /// <summary>A tag chip; null clears the filter.</summary>
    public Action<string?>? OnTagFilter;

    public Action<float>? OnIconSize;
    public Action? OnRefresh;
    public Action? OnOpenSettings;

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

    // The context menu's target and its frozen rows. The array is allocated
    // with the model and REWRITTEN at open, so even the cold right-click path
    // allocates nothing: ContextMenuItem is a struct.
    internal int MenuTile = -1;
    internal bool MenuRequested;
    internal readonly ContextMenuItem[] MenuItems = new ContextMenuItem[3];
}

/// <summary>
/// The pose library, drawn INSIDE the shell's content rect: there is no window
/// and no chassis to inherit, so the view lays its own four bands out in the
/// rectangle it is handed — the search band, the folder rail, the tile grid
/// with its info strip, and the action row.
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

    /// <summary>FilterPill's own left pad; the band's margin tops it up.
    /// </summary>
    private const float SearchInnerPad = 10f;

    private static readonly Action<string> IgnoreQuery = static _ => { };

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

        var rects = Bands(origin, size, scale, theme);
        DrawBand(vm, rects.Band, scale, theme);
        DrawRail(vm, rects.Rail, scale, theme);
        DrawBody(vm, rects.Body, scale, theme);
        DrawActionRow(vm, rects.Footer, scale, theme);
        DrawSizeSlider(vm, rects.Footer, scale, theme);
        DrawMenu(vm);

        if (submit && vm.CanApply && vm.Selected >= 0
            && vm.Selected < vm.Tiles.Count)
            vm.OnApplyTile?.Invoke(vm.Selected);
    }

    /// <summary>
    /// The pane's four bands, and the ink that separates them. This is pane
    /// STRUCTURE, not window chrome: two rules and the rail's raised slab, all
    /// measured from the handed rectangle.
    /// </summary>
    private static WindowFrameRects Bands(
        Vector2 origin, Vector2 size, float scale, Theme theme)
    {
        var max = origin + size;
        float rule = MathF.Max(1f, scale);
        float bandBottom = MathF.Min(max.Y, origin.Y + BandHeight * scale);
        float rowTop = MathF.Max(
            bandBottom, max.Y - theme.Floating.ModalBarHeight * scale);
        // The rail never takes more than half the pane: a narrow workspace
        // keeps a grid rather than becoming a folder list.
        float railWidth = MathF.Min(
            theme.Settings.NavigationWidth * scale, size.X * 0.5f);

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
            draw.AddRectFilled(
                rail.Min, rail.Max, Packed(theme.SurfaceRaised));
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
                new Vector2(origin.X, rowTop + rule), max),
        };
    }

    /// <summary>The action row: the status caption on the left (the size
    /// scrubber is seated beside it separately) and the three commands on the
    /// right.</summary>
    private static void DrawActionRow(
        PoseLibraryViewModel vm, WindowFrameRect footer, float scale, Theme theme)
    {
        if (!(footer.Size.X > 0f) || !(footer.Size.Y > 0f))
            return;
        float inset = theme.Page.Inset * scale;
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
        bool none = vm.Selected < 0 || vm.Selected >= vm.Tiles.Count;
        // Configuring sources belongs where the library is, not only in the
        // empty state a user with sources never sees.
        scope.Button(
            "Add source…",
            vm.SettingsClick!,
            style: ControlStyle.Comfortable);
        scope.Button(
            "Spawn as new",
            vm.SpawnClick!,
            disabled: none || !vm.CanSpawn,
            style: ControlStyle.Comfortable);
        scope.Button(
            vm.ApplyLabel,
            vm.ApplyClick!,
            disabled: none || !vm.CanApply,
            style: ControlStyle.Comfortable,
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
        // The pane's own inset, which is also where the rail's row marks
        // stand: the shell already spent its content inset outside this rect.
        float inset = theme.Page.Inset * scale;
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

        if (vm.ActiveTag is { Length: > 0 } tag)
            right = DrawActiveTag(vm, tag, band, right, scale, theme)
                - gap;

        // The margin makes up FilterPill's own pad, so the search glyph sits
        // where the rail's row marks do.
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
            "Search poses…",
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
        ImGui.SetCursorScreenPos(rail.Min + new Vector2(inset * scale));
        Crystarium.ScrollRegion(
            RailId,
            rail.Size.X / scale - inset * 2f,
            rail.Size.Y / scale - inset * 2f,
            vm.Rail!);
    }

    private static void DrawFolders(
        PoseLibraryViewModel vm, Crystarium.ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float width = region.ContentWidth * scale;

        // The rows stack flush at the row height: the ambient vertical spacing
        // belongs to the surrounding flow, not to the rail.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            for (int i = 0; i < vm.Folders.Count; i++)
                if (FolderRow(
                        vm.Folders[i],
                        // The two synthetic heads are positional by contract,
                        // so the favourites row can carry its own mark without
                        // the binder having to state one per row.
                        i == 1 ? TablerIcon.Star : TablerIcon.Folder,
                        vm.SelectedFolder == i,
                        width,
                        scale,
                        theme))
                    vm.OnSelectFolder?.Invoke(i);
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
        float scale,
        Theme theme)
    {
        float height = theme.Controls.ListRowHeight * scale;
        var hit = Interactive.Reserve(
            row.Key, new Vector2(width, height), disabled: false);

        var fill = selected
            ? theme.Chrome.SidebarSelected
            : hit.Hovered
                ? theme.Chrome.SidebarHover
                : Vector4.Zero;
        if (fill.W > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(
                hit.ScreenMin,
                hit.ScreenMax,
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
        float labelRight = hit.ScreenMax.X;
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

        // Two sources always exist ("All poses", "Favorites"): only those two
        // means nothing was ever scanned, which is a CONFIGURATION answer, not
        // an empty filter.
        if (vm.Folders.Count <= 2)
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

        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            float pad = GridVPad * scale;
            ImGui.Dummy(new Vector2(0f, pad));
            if (vm.Visible.Count == 0)
            {
                EmptyLine(region.ContentWidth, scale, theme);
            }
            else
            {
                int rows = (vm.Visible.Count + columns - 1) / columns;
                var clipper = new ImGuiListClipper();
                clipper.Begin(rows, pitchY * scale);
                while (clipper.Step())
                {
                    for (int r = clipper.DisplayStart;
                         r < clipper.DisplayEnd;
                         r++)
                    {
                        var rowMin = ImGui.GetCursorScreenPos();
                        for (int c = 0; c < columns; c++)
                        {
                            int slot = r * columns + c;
                            if (slot >= vm.Visible.Count)
                                break;
                            Tile(
                                vm,
                                vm.Visible[slot],
                                new Vector2(
                                    rowMin.X + (inset + c * pitchX) * scale,
                                    rowMin.Y),
                                icon,
                                scale,
                                theme);
                        }
                        // Every tile moved the cursor; the row's own pitch is
                        // what the clipper measured, so it is restored here.
                        ImGui.SetCursorScreenPos(new Vector2(
                            rowMin.X, rowMin.Y + pitchY * scale));
                    }
                }
                clipper.End();
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

            if (tile.Favorite || hovered)
                Crystarium.IconIn(
                    starMin,
                    starMax,
                    TablerIcon.Star,
                    tile.Favorite ? theme.Accent : theme.TextMuted,
                    opacity: onStar && hovered ? 1f : 0.8f);

            if (onStar)
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

        float side = MathF.Min(box.X, box.Y) * 0.4f;
        var glyphMin = theme.Optical.Snap(
            boxMin + (box - new Vector2(side)) * 0.5f);
        Crystarium.IconIn(
            glyphMin,
            glyphMin + new Vector2(side),
            tile.Legacy ? TablerIcon.File : TablerIcon.Armature,
            theme.TextDim);
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
        bool clipped = Crystarium.MeasureText(tile.Label, labelStyle).X > band;
        Fitted(
            new Vector2(min.X + pad, top),
            new Vector2(band, LabelLineHeight * scale),
            tile.Label,
            labelStyle,
            TextAlign.Center,
            besideIcon: false);

        Fitted(
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

        if (clipped && hovered)
            Crystarium.HoverHelp.Preview(
                TileLabelHelpId, min, min + size, tile.Label);
    }

    /// <summary>The grid's empty state: one caption on a row band, centred
    /// over the columns that would have been there.</summary>
    private static void EmptyLine(float contentWidth, float scale, Theme theme)
    {
        var min = ImGui.GetCursorScreenPos();
        float height = theme.Controls.ListRowHeight * scale;
        Crystarium.TextInBand(
            min,
            new Vector2(contentWidth * scale, height),
            "No matches.",
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

    /// <summary>
    /// The thumbnail-size scrubber. The footer's ActionBar carries labels and
    /// buttons and nothing else, so the slider is seated directly in the
    /// footer rect, one action gap past where the status caption ends — the
    /// same measurement the bar itself made for that caption.
    /// </summary>
    private static void DrawSizeSlider(
        PoseLibraryViewModel vm,
        WindowFrameRect footer,
        float scale,
        Theme theme)
    {
        if (!(footer.Size.X > 0f))
            return;
        var labelStyle = new TextStyle
        {
            Size = theme.Typography.LabelSize,
            Weight = FontWeight.Regular,
            Color = theme.FormLabel,
        };
        float gap = theme.Page.ActionGap * scale;
        float status = vm.Status.Length == 0
            ? 0f
            : Crystarium.MeasureText(vm.Status, labelStyle).X + gap;
        float height = theme.Controls.SliderHeight * scale;
        float x = footer.Min.X + theme.Page.Inset * scale + status;
        if (x + SliderWidth * scale > footer.Max.X)
            return;

        ImGui.SetCursorScreenPos(new Vector2(
            x, footer.Min.Y + (footer.Size.Y - height) * 0.5f));
        Crystarium.Slider(
            SliderId,
            Math.Clamp(vm.IconSize, MinimumIconSize, MaximumIconSize),
            MinimumIconSize,
            MaximumIconSize,
            vm.IconSizeChange!,
            new ControlStyle { Width = UiWidth.Fixed(SliderWidth) },
            help: "Thumbnail size");
    }

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
    /// clip's snapped edge shaves a fitting run's descender otherwise.
    /// </summary>
    private static void Fitted(
        Vector2 min,
        Vector2 band,
        string text,
        in TextStyle style,
        TextAlign align = TextAlign.Start,
        bool besideIcon = true)
    {
        if (!(band.X > 0f))
            return;
        if (Crystarium.MeasureText(text, style).X <= band.X)
            Crystarium.TextInBand(min, band, text, style, align, besideIcon);
        else
            // The constraint box IS the band, so the run's own alignment
            // inside it is what carries the caller's intent.
            Crystarium.TextInBand(
                min, band, text, style,
                TextConstraint.Truncate(band.X, align),
                TextAlign.Start, besideIcon);
    }

    private static uint Packed(Vector4 color) =>
        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
}
