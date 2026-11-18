using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.AxisWellWidgets;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.PageForm;
using static Poser.UI.Widgets.PopoverWidgets;
using static Poser.UI.Widgets.ScrollRegionWidgets;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

/// <summary>
/// Picks a texture by its number, from a grid that shows what each texture
/// looks like. The field is the number itself, with buttons to step it,
/// because the number is what gets saved. A preview square beside it opens
/// the grid.
///
/// <para>Like <see cref="SearchPicker{T}"/>, it stays alive between frames:
/// <see cref="Draw"/> is called every frame and returns the texture picked,
/// so the row that opened the picker handles the result.
/// </para>
///
/// <para>The game has no list of sky or cloud textures, so the picker tries
/// each number and leaves out the ones the game has no file for. Zero is
/// always kept, because it means no texture. The numbers are tried a few at
/// a time over many frames, because loading a texture preview holds up the
/// frame until it is found, and a thousand in one frame is a visible stutter.</para>
/// </summary>
public sealed class TexturePicker
{
    /// <summary>Three large tiles across and three rows visible, big enough
    /// to make out the pattern of a housing window.
    /// </summary>
    private const int TextureGridColumns = 3;

    private const int TextureGridRows = 3;

    private const float TextureTileSize = 120f;

    /// <summary>The space between tiles comes from the theme's shared
    /// spacing.</summary>
    private static float TextureTileGap => ActiveTheme.Spacing.Four;

    /// <summary>Space between the picture and the edge of its tile, so the
    /// outline of the selected tile never covers the picture.</summary>
    private const float TextureTileInset = 3f;

    /// <summary>Height of the name strip under the picture. The name gets
    /// its own strip, because printed on the picture it would hide part of
    /// it and be hard to read.</summary>
    private const float TextureTileCaption = 16f;

    /// <summary>Padding inside the picker. The shared popup padding is sized
    /// for small menus and looks like none on something this large.</summary>
    private const float TextureSurfacePadding = 12f;

    /// <summary>Maximum catalog probes per frame, including pending retries.
    /// Total loading time depends on the catalog and texture loader.</summary>
    private const int TextureProbesPerFrame = 16;

    /// <summary>
    /// Zero means no texture. It is always offered, whatever the game says
    /// about it. Every other number gets a tile because the game has a file
    /// for it; zero gets one because the grid must offer "none" directly,
    /// not leave it to the step buttons beside it.
    /// </summary>
    private const uint NoTextureId = 0;

    private readonly string _popupId;
    private readonly string _gridId;
    private readonly TexturePreview _preview;
    private readonly uint _count;
    private readonly Func<uint, string>? _caption;
    private readonly Action _body;
    private readonly Action<ScrollRegionScope> _grid;

    /// <summary>The ids the game answered for, ascending.</summary>
    private readonly List<uint> _ids = new();

    /// <summary>The ids still loading, asked again next frame.</summary>
    private readonly List<uint> _loading = new();

    private uint _probeNext;
    private bool _openRequested;
    private Vector2 _anchorMin;
    private Vector2 _anchorMax;
    private uint _selected;
    private uint? _picked;

    /// <summary>The grid's columns and tile side, per picker: a face
    /// grid runs five small tiles across, a sky grid three big ones.</summary>
    private readonly int _columns;
    private readonly int _rows;
    private readonly float _tileSize;

    /// <param name="count">Exclusive upper bound of the ID range to probe.</param>
    /// <param name="caption">Optional label for an ID; otherwise displays the ID.</param>
    /// <param name="knownIds">When supplied, uses this list instead of probing the range.</param>
    public TexturePicker(
        string id, TexturePreview preview, uint count = 1000,
        Func<uint, string>? caption = null,
        int columns = TextureGridColumns, float tileSize = TextureTileSize,
        int rows = TextureGridRows, IReadOnlyList<uint>? knownIds = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _columns = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        _tileSize = tileSize;
        // When the numbers are known in advance, nothing is tried: the grid
        // opens at its full size on the first frame, and a tile whose picture
        // is still loading shows an icon until it is ready. When numbers must
        // be tried, as for sky and clouds, the grid grows over a dozen frames.
        if (knownIds is not null)
        {
            _ids.AddRange(knownIds);
            _ids.Sort();
            _probeNext = count;
        }
        _popupId = $"##texture-picker-{id}";
        _gridId = $"{_popupId}-grid";
        _preview = preview;
        _count = count;
        _caption = caption;
        _body = DrawBody;
        _grid = DrawGrid;
    }

    public bool IsOpen => ImGui.IsPopupOpen(_popupId);

    /// <summary>Draws the picker if it is open and returns the texture
    /// picked, if any. Call it every frame: ImGui closes a popup that is not
    /// drawn in a frame, and a closed picker returns at once.</summary>
    public uint? Draw()
    {
        if (_openRequested)
        {
            _openRequested = false;
            OpenPopover(_popupId);
        }
        if (!ImGui.IsPopupOpen(_popupId))
            return null;

        Probe();

        var theme = ActiveTheme;
        float pad = TextureSurfacePadding;
        float pitch = _tileSize + TextureTileCaption + TextureTileGap;
        // The space kept for the scrollbar sits at the right edge and serves
        // as the right padding, so the padding is only added on the other
        // three sides.
        float width = pad
            + _columns * (_tileSize + TextureTileGap)
            - TextureTileGap
            + theme.Scrollbar.GutterWidth;
        // The picker is as tall as its rows, up to the row limit, and only
        // scrolls past that.
        float height = pad * 2f
            + RowsShown() * pitch - TextureTileGap;

        _picked = null;
        FloatingSurface.Popup(
            _popupId,
            new FloatingSurfaceProps
            {
                Width = width,
                Height = height,
                // No window padding: the grid places itself, so the
                // scrollbar space can reach the right edge.
                Padding = 0f,
                AnchorMin = _anchorMin,
                AnchorMax = _anchorMax,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            _body);
        return _picked;
    }

    /// <summary>
    /// The control in a form row: the preview square that opens the grid,
    /// then the number between a minus and a plus button. The preview comes
    /// first because it is what people look at; the number follows because it
    /// is the value that is saved.
    /// </summary>
    public void Field(
        in FormPairCell cell,
        uint value,
        Action<uint> onChange,
        bool disabled = false,
        string? help = null)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var theme = ActiveTheme;
        float scale = cell.Scale;
        float side = theme.Controls.WorkspaceHeight * scale;
        float gap = theme.Page.ActionGap * scale;
        // The minus and plus buttons sit close to the number; the preview
        // square stands a little apart.
        float tight = theme.Spacing.One * scale;
        float committed = side * 3f + gap + tight * 2f;
        float well = Math.Clamp(
            cell.Width - committed,
            theme.Controls.WorkspaceHeight * scale,
            theme.Form.ValueColumnWidth * scale);
        float top = cell.Center(theme.Controls.WorkspaceHeight).Y;
        float x = cell.Origin.X;

        DrawTile(
            $"{_popupId}-trigger", value, new Vector2(x, top),
            new Vector2(side), disabled, help);
        x += side + gap;

        var square = ControlStyle.Square(theme.Controls.WorkspaceHeight);
        ImGui.SetCursorScreenPos(new Vector2(x, top));
        IconButton(
            TablerIcon.Minus,
            () => onChange(value == 0 ? 0 : value - 1),
            square,
            disabled || value == 0,
            "Step down one id",
            id: $"{_popupId}-down");
        x += side + tight;

        ImGui.SetCursorScreenPos(new Vector2(x, top));
        AxisWell(
            $"{_popupId}-well",
            string.Empty,
            value,
            next => onChange(Clamp(next)),
            null,
            ActiveTheme.FormValue,
            0.25f,
            "0",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(well / scale),
            },
            disabled);
        x += well + tight;

        ImGui.SetCursorScreenPos(new Vector2(x, top));
        IconButton(
            TablerIcon.Plus,
            () => onChange(Clamp(value + 1f)),
            square,
            disabled || value + 1 >= _count,
            "Step up one id",
            id: $"{_popupId}-up");
    }

    private uint Clamp(float value) => (uint)Math.Clamp(
        (int)MathF.Round(value), 0, (int)Math.Max(1u, _count) - 1);

    /// <summary>Opens the picker under the given square. Passing its corners
    /// means controls drawn later cannot move where the picker opens.</summary>
    public void OpenAt(uint selected, Vector2 anchorMin, Vector2 anchorMax) =>
        Open(selected, anchorMin, anchorMax);

    private void Open(uint selected, Vector2 min, Vector2 max)
    {
        _selected = selected;
        _anchorMin = min;
        _anchorMax = max;
        _openRequested = true;
    }

    /// <summary>
    /// Checks again on textures still loading, then tries more numbers. Both
    /// count toward the limit per frame; textures still loading are checked
    /// again on later frames.
    /// </summary>
    private void Probe()
    {
        int budget = TextureProbesPerFrame;
        for (int i = _loading.Count - 1; i >= 0 && budget > 0; i--)
        {
            budget--;
            uint id = _loading[i];
            var answer = _preview(id, out _, out _);
            if (answer == TextureProbe.Pending)
                continue;
            _loading.RemoveAt(i);
            if (answer == TextureProbe.Ready || id == NoTextureId)
                Admit(id);
        }
        while (budget > 0 && _probeNext < _count)
        {
            budget--;
            uint id = _probeNext++;
            var answer = _preview(id, out _, out _);
            if (answer == TextureProbe.Pending)
                _loading.Add(id);
            else if (answer == TextureProbe.Ready || id == NoTextureId)
                Admit(id);
        }
    }

    /// <summary>Numbers arrive out of order, because a texture that finished
    /// loading late comes after numbers tried since. So the list is kept
    /// sorted instead of only added to at the end.</summary>
    private void Admit(uint id)
    {
        int index = _ids.BinarySearch(id);
        if (index < 0)
            _ids.Insert(~index, id);
    }

    private int RowsShown() =>
        Math.Clamp((_ids.Count + _columns - 1) / _columns, 1, _rows);

    private void DrawBody()
    {
        var min = ImGui.GetWindowPos();
        float scale = ImGuiHelpers.GlobalScale;
        float pitch = _tileSize + TextureTileCaption + TextureTileGap;
        // Left and top padding are added here by hand, because the window has
        // none. The right padding is the space kept for the scrollbar.
        ImGui.SetCursorScreenPos(
            min + new Vector2(TextureSurfacePadding * scale));
        ScrollRegion(
            _gridId,
            _columns * (_tileSize + TextureTileGap)
                - TextureTileGap
                + ActiveTheme.Scrollbar.GutterWidth,
            RowsShown() * pitch - TextureTileGap,
            _grid);
        if (_picked != null)
            ImGui.CloseCurrentPopup();
    }

    /// <summary>Draws only the rows of the grid that are on screen, so a
    /// thousand textures cost no more than the visible ones.</summary>
    private void DrawGrid(ScrollRegionScope region)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float side = _tileSize * scale;
        float acrossPitch = (_tileSize + TextureTileGap) * scale;
        float pitch =
            (_tileSize + TextureTileCaption + TextureTileGap) * scale;
        if (_ids.Count == 0)
        {
            region.Empty(
                _probeNext < _count || _loading.Count > 0
                    ? "Reading the game's textures…"
                    : "The game has no textures for this slot.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            int rows =
                (_ids.Count + _columns - 1) / _columns;
            var clipper = new ImGuiListClipper();
            clipper.Begin(rows, pitch);
            while (clipper.Step())
            {
                for (int r = clipper.DisplayStart;
                    r < clipper.DisplayEnd;
                    r++)
                {
                    var band = ImGui.GetCursorScreenPos();
                    for (int c = 0; c < _columns; c++)
                    {
                        int index = r * _columns + c;
                        if (index >= _ids.Count)
                            break;
                        DrawTile(
                            $"{_popupId}-tile-{_ids[index]}",
                            _ids[index],
                            new Vector2(band.X + c * acrossPitch, band.Y),
                            new Vector2(side),
                            disabled: false,
                            help: null,
                            grid: true);
                    }
                    ImGui.SetCursorScreenPos(
                        new Vector2(band.X, band.Y + pitch));
                }
            }
            clipper.End();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>
    /// The part of the texture a tile shows. Some of these textures are
    /// animation sheets: square frames side by side in one wide picture.
    /// Squeezing a whole sheet into a square tile would show every frame at
    /// once. So a texture that is not square shows only its top-left square,
    /// which is the first frame; a square texture shows in full.
    ///
    /// <para>This only changes what the tile shows, not the number that is
    /// saved. No pixels are read, resized or stored: ImGui is only told which
    /// corners of the picture to draw. A texture whose size is unknown reports
    /// zero and is drawn whole.</para>
    /// </summary>
    private static (Vector2 Min, Vector2 Max) FirstFrameUv(Vector2 pixels)
    {
        if (pixels.X <= 0f || pixels.Y <= 0f || pixels.X == pixels.Y)
            return (Vector2.Zero, Vector2.One);
        float side = MathF.Min(pixels.X, pixels.Y);
        return (
            Vector2.Zero,
            new Vector2(side / pixels.X, side / pixels.Y));
    }

    /// <summary>
    /// One clickable preview square. The square in the form row opens the
    /// grid; a square in the grid picks that texture. The texture is looked up
    /// again every frame, because a shared texture's handle is only good for
    /// the frame it was given in.
    /// </summary>
    private void DrawTile(
        string id,
        uint value,
        Vector2 min,
        Vector2 size,
        bool disabled,
        string? help,
        bool grid = false)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        // A grid tile is the picture plus the name under it. The whole tile
        // can be clicked, the name as well as the picture.
        float captionBand = grid ? TextureTileCaption * scale : 0f;
        var cell = size + new Vector2(0f, captionBand);
        var max = min + cell;
        var artOuterMax = min + size;
        bool active = grid && value == _selected;

        ImGui.SetCursorScreenPos(min);
        disabled |= Interactive.GreyedOut;
        var hit = Interactive.Reserve(id, cell, disabled);

        var fill = hit.Hovered || hit.Active
            ? theme.Chrome.WeakOverlay
            : active ? theme.Chrome.ActiveOverlay : theme.Chrome.InputWell;
        BoxRenderer.Draw(draw, min, max, new BoxStyle
        {
            BackgroundColor = disabled
                ? fill.Fade(theme.Chrome.DisabledOpacity)
                : fill,
            BorderRadius = theme.Radii.Control,
            BorderWidth = active ? 1f : 0f,
            BorderTopColor = active ? theme.Chrome.Primary : null,
            BorderRightColor = active ? theme.Chrome.Primary : null,
            BorderBottomColor = active ? theme.Chrome.Primary : null,
            BorderLeftColor = active ? theme.Chrome.Primary : null,
        });

        float inset = TextureTileInset * scale;
        var artMin = min + new Vector2(inset);
        var artMax = artOuterMax - new Vector2(inset);
        bool drawn = _preview(value, out nint handle, out var pixels)
            == TextureProbe.Ready && handle != 0;
        if (drawn)
        {
            var (uvMin, uvMax) = FirstFrameUv(pixels);
            draw.AddImage(
                new ImTextureID(handle),
                artMin,
                artMax,
                uvMin,
                uvMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(
                        disabled
                            ? Vector4.One.Fade(
                                theme.Chrome.DisabledOpacity)
                            : Vector4.One)));
        }
        else
            IconIn(
                (min + artOuterMax) * 0.5f - new Vector2(
                    theme.Controls.IconSize * 0.5f * scale),
                (min + artOuterMax) * 0.5f + new Vector2(
                    theme.Controls.IconSize * 0.5f * scale),
                TablerIcon.Photo,
                theme.TextDim);

        if (grid)
        {
            // The name sits under the picture on the tile's own background,
            // so the picture stays whole and the name stays readable next to
            // a bright picture. Textures with names show their name; the
            // others show their number. A name too wide for the tile is
            // shortened with an ellipsis.
            // Zero with no picture is the no-texture choice and says None,
            // because a bare number under an empty square would look like a
            // texture the game is missing.
            bool none = value == NoTextureId && !drawn;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Family = _caption is null && !none
                    ? FontFamily.Mono
                    : FontFamily.Default,
                Color = theme.TextDim,
            };
            TextInBand(
                new Vector2(artMin.X, artOuterMax.Y),
                new Vector2(artMax.X - artMin.X, captionBand),
                TruncateText(
                    none
                        ? "None"
                        : _caption?.Invoke(value)
                            ?? value.ToString(
                                "D3", CultureInfo.InvariantCulture),
                    captionStyle,
                    artMax.X - artMin.X),
                captionStyle,
                TextAlign.Center);
        }

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, disabled, min, max))
            HoverHelp.Explain(id, min, max, help!);

        if (!hit.Clicked || disabled)
            return;
        if (grid)
            _picked = value;
        else
            Open(value, min, max);
    }
}
