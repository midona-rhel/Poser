using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>What one candidate texture id answered when it was probed.</summary>
public enum TextureProbe
{
    /// <summary>The game has no such texture: the id is dropped for good.
    /// </summary>
    Missing,

    /// <summary>The texture exists but its wrap is not ready this frame, so
    /// the id is asked again. A game texture is loaded asynchronously, which
    /// makes this the answer EVERY id gives the first time it is asked.
    /// </summary>
    Pending,

    /// <summary>Resolved: the handle is this frame's and must not be kept.
    /// </summary>
    Ready,
}

/// <summary>Resolves one candidate texture id to a frame-local ImGui handle
/// and the PIXEL SIZE of the image behind it. Stated by the caller, exactly as
/// <see cref="PickerOptions{T}.Texture"/> is — game paths and the texture
/// service stay outside Crystarium.
///
/// <para>The size is what tells a picture from an ANIMATION ATLAS, which the
/// tile must sample differently; an unresolved probe answers
/// <see cref="Vector2.Zero"/> and is drawn whole.</para></summary>
public delegate TextureProbe TexturePreview(
    uint id, out nint handle, out Vector2 pixels);

public static partial class Crystarium
{
    /// <summary>
    /// ONE texture id, chosen off a grid of what the textures actually look
    /// like. The field is a stepped numeric id — the id IS the value, and a
    /// pose file states it — with a preview tile beside it that opens the grid.
    ///
    /// <para>Retained like <see cref="SearchPicker{T}"/> and drained the same
    /// way: <see cref="Draw"/> runs the surface's lifetime once a frame and
    /// answers with the pick, so the row that opened it reports the outcome.
    /// </para>
    ///
    /// <para>The catalog is PROBED rather than known: the game exposes no list
    /// of sky or cloud textures, so the ids are walked and the ones the game
    /// has no file for are dropped — every id but zero, which is the
    /// no-texture choice and is always offered. The walk is chunked over
    /// frames because a texture wrap blocks while it resolves — a thousand of
    /// them on one frame is a visible hitch (Ktisis staggers the same walk
    /// off-thread).</para>
    /// </summary>
    public sealed class TexturePicker
    {
        /// <summary>Three large tiles across, three rows in the viewport —
        /// big enough that a housing window's leadwork actually reads.
        /// </summary>
        private const int TextureGridColumns = 3;

        private const int TextureGridRows = 3;

        private const float TextureTileSize = 120f;

        /// <summary>Inter-tile breathing is the shared spacing token, not a
        /// metric of this component's own.</summary>
        private static float TextureTileGap => ActiveTheme.Spacing.Four;

        /// <summary>The tile's art breathes off its own hover fill, so the
        /// selected ring never crops the preview.</summary>
        private const float TextureTileInset = 3f;

        /// <summary>The caption band UNDER the art — its own strip of panel,
        /// never a scrim over the preview: a name printed on the art both
        /// hides it and fights it for contrast.</summary>
        private const float TextureTileCaption = 16f;

        /// <summary>The surface's own inset. The shared PopupPadding is
        /// menu-tier (4) and reads as none on a surface this large.</summary>
        private const float TextureSurfacePadding = 12f;

        /// <summary>Ids probed per frame. A whole catalog resolves inside a
        /// second at this rate and no frame carries more than a handful of
        /// blocking wrap resolutions.</summary>
        private const int TextureProbesPerFrame = 16;

        /// <summary>
        /// Zero is NO TEXTURE and is admitted whatever the game answers for
        /// it — the one id the walk may not drop. Every other id earns its
        /// tile by having a file; zero earns its tile by being the value that
        /// means "none", which the grid must be able to reach (user
        /// 2026-08-14) rather than leaving to the steppers beside it. Ktisis
        /// keeps the same id for the same reason.
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

        /// <param name="count">How many ids to walk. Ktisis walks 0..999 for
        /// every one of these catalogs and drops what the game does not
        /// have.</param>
        /// <param name="caption">The tile's caption for an id — a catalog
        /// whose entries have NAMES states them here; unset, the id itself
        /// is the caption.</param>
        public TexturePicker(
            string id, TexturePreview preview, uint count = 1000,
            Func<uint, string>? caption = null)
        {
            ArgumentNullException.ThrowIfNull(preview);
            _popupId = $"##texture-picker-{id}";
            _gridId = $"{_popupId}-grid";
            _preview = preview;
            _count = count;
            _caption = caption;
            _body = DrawBody;
            _grid = DrawGrid;
        }

        public bool IsOpen => ImGui.IsPopupOpen(_popupId);

        /// <summary>Draws the surface if it is up, and answers with the pick.
        /// UNCONDITIONAL like every retained surface: a popup that is not
        /// submitted on a frame is closed by ImGui at the end of it, and a
        /// closed picker returns on its first line.</summary>
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
            float pitch = TextureTileSize + TextureTileCaption + TextureTileGap;
            // The gutter-as-padding contract: the reserved scrollbar gutter
            // sits ON the surface's right edge and IS the trailing inset —
            // the stated padding covers the other three sides only.
            float width = pad
                + TextureGridColumns * (TextureTileSize + TextureTileGap)
                - TextureTileGap
                + theme.Scrollbar.GutterWidth;
            float height = pad * 2f
                + TextureGridRows * pitch - TextureTileGap;

            _picked = null;
            FloatingSurface.Popup(
                _popupId,
                new FloatingSurfaceProps
                {
                    Width = width,
                    Height = height,
                    // Zero window padding: the body seats itself so the
                    // gutter can reach the edge the padding would cover.
                    Padding = 0f,
                    AnchorMin = _anchorMin,
                    AnchorMax = _anchorMax,
                    // OPAQUE, for the reason SearchPicker's panel is: glass
                    // let the page bleed through in game.
                    Treatment = FloatingSurfaceTreatment.Unframed,
                },
                _body);
            return _picked;
        }

        /// <summary>
        /// The row control: the preview tile that opens the grid, then the id
        /// itself under a pair of steppers. The tile leads because it is what
        /// the eye reads; the id trails because it is what the value IS.
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
            // The stepper hugs the well it steps; the tile stands apart from
            // the group it previews.
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
            Crystarium.IconButton(
                TablerIcon.Minus,
                () => onChange(value == 0 ? 0 : value - 1),
                square,
                disabled || value == 0,
                "Step down one id",
                id: $"{_popupId}-down");
            x += side + tight;

            ImGui.SetCursorScreenPos(new Vector2(x, top));
            Crystarium.AxisWell(
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
            Crystarium.IconButton(
                TablerIcon.Plus,
                () => onChange(Clamp(value + 1f)),
                square,
                disabled || value + 1 >= _count,
                "Step up one id",
                id: $"{_popupId}-up");
        }

        private uint Clamp(float value) => (uint)Math.Clamp(
            (int)MathF.Round(value), 0, (int)Math.Max(1u, _count) - 1);

        /// <summary>Arms the surface under the tile that owns it. The anchor is
        /// the TILE's rect rather than the last reserved item, so the trailing
        /// steppers cannot move the surface.</summary>
        private void Open(uint selected, Vector2 min, Vector2 max)
        {
            _selected = selected;
            _anchorMin = min;
            _anchorMax = max;
            _openRequested = true;
        }

        /// <summary>
        /// One chunk of the catalog walk. The loading list is retried FIRST:
        /// every id answers Pending the first time it is asked, so the walk
        /// would otherwise finish having admitted nothing.
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

        /// <summary>Ids are admitted out of order — a retry lands after the ids
        /// the walk reached meanwhile — so the list is kept sorted rather than
        /// appended to.</summary>
        private void Admit(uint id)
        {
            int index = _ids.BinarySearch(id);
            if (index < 0)
                _ids.Insert(~index, id);
        }

        private void DrawBody()
        {
            var min = ImGui.GetWindowPos();
            float scale = ImGuiHelpers.GlobalScale;
            PaintPanel(
                ImGui.GetWindowDrawList(), min, min + ImGui.GetWindowSize(),
                ActiveTheme);
            float pitch = TextureTileSize + TextureTileCaption + TextureTileGap;
            // Left and top insets by hand (the window's own padding is zero);
            // the right inset is the ScrollRegion's reserved gutter itself.
            ImGui.SetCursorScreenPos(
                min + new Vector2(TextureSurfacePadding * scale));
            ScrollRegion(
                _gridId,
                TextureGridColumns * (TextureTileSize + TextureTileGap)
                    - TextureTileGap
                    + ActiveTheme.Scrollbar.GutterWidth,
                TextureGridRows * pitch - TextureTileGap,
                _grid);
            if (_picked != null)
                ImGui.CloseCurrentPopup();
        }

        /// <summary>The grid, clipped at the ROW pitch: a catalog of a thousand
        /// tiles submits only the band the viewport shows.</summary>
        private void DrawGrid(ScrollRegionScope region)
        {
            float scale = ImGuiHelpers.GlobalScale;
            float side = TextureTileSize * scale;
            float acrossPitch = (TextureTileSize + TextureTileGap) * scale;
            float pitch =
                (TextureTileSize + TextureTileCaption + TextureTileGap) * scale;
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
                    (_ids.Count + TextureGridColumns - 1) / TextureGridColumns;
                var clipper = new ImGuiListClipper();
                clipper.Begin(rows, pitch);
                while (clipper.Step())
                {
                    for (int r = clipper.DisplayStart;
                        r < clipper.DisplayEnd;
                        r++)
                    {
                        var band = ImGui.GetCursorScreenPos();
                        for (int c = 0; c < TextureGridColumns; c++)
                        {
                            int index = r * TextureGridColumns + c;
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
        /// The corners of the image a tile actually samples. Some of the
        /// game's textures in these catalogs are ANIMATION ATLASES — a wide
        /// sheet of square frames laid side by side — and squashing a whole
        /// sheet into a square tile shows every frame at once instead of the
        /// picture. Anything that is not 1:1 is therefore sampled at its
        /// TOP-LEFT SQUARE, which is the atlas's first frame; a square texture
        /// keeps the whole image.
        ///
        /// <para>DISPLAY ONLY, and free: the id the scene is given never sees
        /// this, and the crop is two UV corners handed to ImGui — no pixels
        /// are decoded, resized, or cached. A probe that could not state a
        /// size answers zero and is drawn whole.</para>
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
        /// One preview square, hit-tested. The FIELD's tile opens the surface;
        /// a GRID tile picks. The wrap is re-resolved every frame — a shared
        /// texture's handle is the frame's and nothing else.
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
            // A grid cell is the art square PLUS its caption band; the whole
            // cell is one hit target so the name is as clickable as the art.
            float captionBand = grid ? TextureTileCaption * scale : 0f;
            var cell = size + new Vector2(0f, captionBand);
            var max = min + cell;
            var artOuterMax = min + size;
            bool active = grid && value == _selected;

            ImGui.SetCursorScreenPos(min);
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
                // The caption stands UNDER the art on the tile's own fill —
                // the art stays whole and the text never fights a bright
                // preview for contrast. A named catalog prints its name; a
                // walked one prints the id. A name wider than the tile is
                // cut to it with the shared ellipsis shaping, never drawn
                // past the cell.
                // Zero with no art behind it IS the no-texture choice, and it
                // says so in words: an id printed under an empty square is
                // indistinguishable from a texture the client is missing.
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
}
