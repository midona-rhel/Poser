using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>One head strip: a segmented control shown under the search field.
/// A null strip declares nothing and costs the surface no height.</summary>
public readonly record struct PickerStrip(
    string[] Labels, int Selected, Action<int> OnChange);

/// <summary>
/// The picker's OPTIONAL capabilities, all defaulted off. A caller that states
/// none of them opens the plain name list; a catalog surface states its own
/// query, the row marks, the badge and its strips.
/// </summary>
public record struct PickerOptions<T> where T : class
{
    /// <summary>The caller's OWN filter, told the field's query and answering
    /// with the whole visible list. REPLACES the built-in name contains — a
    /// catalog that matches ids and narrows by kind is not a predicate over a
    /// label.</summary>
    public Func<string, IReadOnlyList<T>>? Query;

    /// <summary>Resolved game texture for the row's 16px mark. Wins over
    /// <see cref="Glyph"/>, which is the fallback for rows with no image.
    /// </summary>
    public Func<T, nint>? Texture;

    public Func<T, TablerIcon?>? Glyph;

    /// <summary>Right-aligned mono readout.</summary>
    public Func<T, string?>? Badge;

    public PickerStrip? Strip;
    public PickerStrip? SecondStrip;

    /// <summary>Logical panel width; 0 takes the theme's picker width.</summary>
    public float Width;
}

public static partial class LegacyCrystarium
{
    // ---- OverlayShell geometry ------------------------------------------
    /// <summary><c>.header</c>: the caption band the MULTI variant carries.
    /// </summary>
    private const float PickerHeaderHeight = 40f;

    /// <summary><c>.searchArea</c>/<c>.searchRow</c>, which is also
    /// GlassInput's natural search height.</summary>
    private const float PickerSearchHeight = 36f;

    /// <summary><c>.checkRow</c> — the PILL's own height.</summary>
    private const float PickerRowHeight = 28f;

    /// <summary>The pill breathes 2px off each neighbouring row, so the list's
    /// pitch is the pill plus both gaps.</summary>
    private const float PickerPillVGap = 2f;

    private const float PickerRowPitch = PickerRowHeight + PickerPillVGap * 2f;

    /// <summary><c>.checkBox</c> is 14px, and the single-select tick occupies
    /// the SAME slot so both variants' labels line up.</summary>
    private const float PickerCheckSlot = 14f;

    /// <summary>The check slot breathes its own square inset INSIDE the pill,
    /// which lands it at the gutter base under the search glyph.</summary>
    private const float PickerRowPadding =
        (PickerRowHeight - PickerCheckSlot) * 0.5f;

    /// <summary>The picker's bar is HALF the shell gutter, and the pill
    /// breathes that same half against the panel's left edge.</summary>
    private const float PickerBarShare = 0.5f;

    /// <summary>The list breathes against the chrome above and the panel
    /// bottom.</summary>
    private const float PickerListVPad = 4f;

    /// <summary>The clear cross breathes off the gutter instead of sitting
    /// flush against it.</summary>
    private const float PickerSearchClearPad = 6f;

    /// <summary>FilterPill's own left pad; the search row's margin tops it up
    /// to the gutter base.</summary>
    private const float PickerSearchInnerPad = 10f;

    /// <summary>The tick inside the slot, at the stroke that keeps a glyph
    /// that small legible.</summary>
    private const float PickerCheckGlyph = 10f;

    private const float PickerCheckStroke = 3f;

    /// <summary>MEASURED: the field's ink centres 2px low in its band, and
    /// lifting the BOX took the glyph with it — so the rise goes to the text
    /// alone through the field's own knob.</summary>
    private const float PickerSearchTextRise = -2f;

    /// <summary>
    /// THE picker. Every variant — single-select, multi-select, a catalog with
    /// marks and head strips — is this object told different things, because
    /// they share everything except what a row's activation MEANS: a pick
    /// decides and closes, a toggle does not.
    ///
    /// <para>Retained, and that is what makes anchoring trivial:
    /// <see cref="Open"/> samples the rect of the item just reserved (the
    /// trigger), and <see cref="Draw"/> runs the popup lifetime once a frame
    /// from then on.</para>
    ///
    /// <para>Both shapes are CONTROLLED. The only state the picker owns is the
    /// filter query, a draft nobody outside the open surface can act on; a
    /// multi caller keeps its own key set and is told each flip.</para>
    /// </summary>
    public sealed class SearchPicker<T> where T : class
    {
        private readonly string _popupId;
        private readonly string _filterId;
        private readonly string _listId;

        // Retained delegates: the popup, the scroll region and the field all
        // take callbacks, and a closure per frame would be this control's whole
        // warm-frame cost.
        private readonly Action _body;
        private readonly Action<ScrollRegionScope> _list;
        private readonly Action<string> _onQuery;

        // The visible list, refilled in place. A caller-supplied Query answers
        // with its own list and this one is left alone.
        private readonly List<T> _filtered = new();

        private bool _openRequested;
        private Vector2 _anchorMin;
        private Vector2 _anchorMax;
        private string _owner = string.Empty;
        private string _query = string.Empty;

        private string? _caption;
        private IReadOnlyList<T> _items = Array.Empty<T>();
        private Func<T, string> _label = static _ => string.Empty;
        private Func<T, string> _key = static _ => string.Empty;
        private string? _selectedKey;
        private IReadOnlySet<string>? _selectedKeys;
        private Action<T, bool>? _onToggle;
        private string? _loadError;
        private PickerOptions<T> _options;

        // Per-frame draw state, written by Draw and read by the two callbacks.
        private IReadOnlyList<T> _visible = Array.Empty<T>();
        private T? _picked;
        private float _panelWidth;
        private float _listHeight;

        public SearchPicker(string id)
        {
            _popupId = $"##search-picker-{id}";
            _filterId = $"{_popupId}-filter";
            _listId = $"{_popupId}-list";
            _body = DrawBody;
            _list = DrawRows;
            _onQuery = next => _query = next;
        }

        /// <summary>Who the open surface belongs to, null while it is closed —
        /// a caller driving several rows off one picker asks this before it
        /// acts on a pick.</summary>
        public string? Owner { get; private set; }

        public bool IsOpen => ImGui.IsPopupOpen(_popupId);

        /// <summary>
        /// Single-select: a row picks its item and closes, and the chosen row
        /// carries a tick. CAPTIONLESS by rule — the trigger that opened the
        /// surface already names the pick.
        /// </summary>
        public void Open(
            string owner,
            IReadOnlyList<T> items,
            Func<T, string> label,
            Func<T, string>? key = null,
            string? selectedKey = null,
            string? loadError = null,
            in PickerOptions<T> options = default)
        {
            Arm(owner, null, items, label, key, loadError, in options);
            _selectedKey = selectedKey;
            _selectedKeys = null;
            _onToggle = null;
        }

        /// <summary>
        /// Multi-select: a row toggles its checkbox and the surface stays. The
        /// caller owns the selection as a SET OF KEYS — held by reference, so a
        /// set mutated in place is seen the same frame — and is told each flip
        /// as <c>(item, selected)</c>.
        /// </summary>
        public void OpenMulti(
            string owner,
            string caption,
            IReadOnlyList<T> items,
            Func<T, string> label,
            Func<T, string> key,
            IReadOnlySet<string> selectedKeys,
            Action<T, bool> onToggle,
            string? loadError = null,
            in PickerOptions<T> options = default)
        {
            ArgumentNullException.ThrowIfNull(selectedKeys);
            ArgumentNullException.ThrowIfNull(onToggle);
            Arm(owner, caption, items, label, key, loadError, in options);
            _selectedKey = null;
            _selectedKeys = selectedKeys;
            _onToggle = onToggle;
        }

        /// <summary>
        /// Re-states the options of an OPEN surface. The strips are controlled
        /// like everything else here — their selection lives in the caller — so
        /// a segment click has to reach the next frame's draw. A caller with
        /// strips calls this each frame before <see cref="Draw"/>; a caller
        /// without them never needs it.
        /// </summary>
        public void Update(in PickerOptions<T> options)
        {
            if (ImGui.IsPopupOpen(_popupId))
                _options = options;
        }

        /// <summary>Draws the surface if it is up. Returns the single-select
        /// pick — a multi toggle reports through its own callback, because a
        /// toggle is not the end of the interaction.</summary>
        public (string Owner, T Item)? Draw()
        {
            if (_openRequested)
            {
                _openRequested = false;
                OpenPopover(_popupId);
            }
            if (!ImGui.IsPopupOpen(_popupId))
            {
                Owner = null;
                return null;
            }
            Owner = _owner;

            var theme = ActiveTheme;
            _panelWidth = _options.Width > 0f ? _options.Width : theme.Picker.Width;
            // The list the surface is SHOWING. A caller that owns its filter
            // already applied the query; the built-in name contains is the
            // DEFAULT filter, not a second one. Every count below — the panel's
            // height included — is that list's.
            _visible = _options.Query is { } query ? query(_query) : Filter();

            int rows = Math.Clamp(
                _visible.Count,
                theme.Picker.MinimumRows,
                theme.Picker.MaximumRows);
            // The panel is its OWN composition's height: chrome plus padded
            // rows. The pad is the LIST's and the row term is the PILL's, not
            // the pitch's — which is why a full list always keeps the last
            // pill's breathing scrollable.
            _listHeight = PickerListVPad * 2f + rows * PickerRowHeight;
            float panelHeight =
                (_caption is null ? 0f : PickerHeaderHeight)
                + StripCount() * StripHeight()
                + PickerSearchHeight
                + _listHeight;

            _picked = null;
            // OPAQUE panel: the glass shell let the page bleed through in game,
            // so the treatment is bare and this class paints fill, border and
            // shadows itself.
            FloatingSurface.Popup(
                _popupId,
                new FloatingSurfaceProps
                {
                    Width = _panelWidth,
                    Height = panelHeight,
                    Padding = 0f,
                    AnchorMin = _anchorMin,
                    AnchorMax = _anchorMax,
                    Treatment = FloatingSurfaceTreatment.Unframed,
                },
                _body);
            _visible = Array.Empty<T>();
            return _picked is { } item ? (_owner, item) : null;
        }

        private void Arm(
            string owner,
            string? caption,
            IReadOnlyList<T> items,
            Func<T, string> label,
            Func<T, string>? key,
            string? loadError,
            in PickerOptions<T> options)
        {
            _anchorMin = ImGui.GetItemRectMin();
            _anchorMax = ImGui.GetItemRectMax();
            _owner = owner;
            _caption = caption;
            _items = items;
            _label = label;
            if (key != null)
                _key = key;
            _loadError = loadError;
            _options = options;
            // The query is a DRAFT: it means nothing outside the open surface,
            // so each open starts empty. Strip selections are the caller's and
            // persist by construction.
            _query = string.Empty;
            _openRequested = true;
        }

        private int StripCount() =>
            (_options.Strip is null ? 0 : 1)
            + (_options.SecondStrip is null ? 0 : 1);

        /// <summary>A strip is a segmented pill breathing the list's own
        /// vertical pad on each side.</summary>
        private static float StripHeight() =>
            ActiveTheme.Controls.NavigationHeight + PickerListVPad * 2f;

        private IReadOnlyList<T> Filter()
        {
            _filtered.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                T item = _items[i];
                if (_query.Length == 0
                    || _label(item).Contains(
                        _query, StringComparison.OrdinalIgnoreCase))
                    _filtered.Add(item);
            }
            return _filtered;
        }

        // ---- the surface -------------------------------------------------
        private void DrawBody()
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = ActiveTheme;
            var draw = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            PaintPanel(draw, min, min + ImGui.GetWindowSize(), theme);

            var origin = ImGui.GetCursorScreenPos();
            // EVERY line in the surface starts on one x: the scroll gutter is
            // the base, and the bar appearing never reflows content.
            float inset = theme.Scrollbar.GutterWidth;
            float pillInset = inset * PickerBarShare;
            float rowWidth = MathF.Max(0f, _panelWidth - pillInset * 2f);
            float y = 0f;

            if (_caption is { } caption)
            {
                var band = new Vector2(_panelWidth, PickerHeaderHeight) * scale;
                PaintRule(draw, origin, band, scale, theme);
                LabelInBand(
                    origin + new Vector2(inset * scale, 0f),
                    new Vector2((_panelWidth - inset * 2f) * scale, band.Y),
                    caption,
                    new TextStyle
                    {
                        Size = theme.Typography.LabelSize,
                        Color = theme.TextMuted,
                    });
                y += PickerHeaderHeight;
            }

            // The island's own left pad is FilterPill's 10; a margin makes up
            // the difference so the search glyph sits over the CHECK slots and
            // the search text over the labels. The extra right inset is the
            // clear cross's breathing.
            float searchMargin = MathF.Max(
                0f, pillInset + PickerRowPadding - PickerSearchInnerPad);
            var searchOrigin = origin + new Vector2(0f, y * scale);
            PaintRule(
                draw,
                searchOrigin,
                new Vector2(_panelWidth, PickerSearchHeight) * scale,
                scale,
                theme);
            ImGui.SetCursorScreenPos(
                searchOrigin + new Vector2(searchMargin * scale, 0f));
            FilterPill(
                _filterId,
                _query,
                _onQuery,
                "Search by name",
                new ControlStyle
                {
                    Width = UiWidth.Fixed(
                        _panelWidth - searchMargin - inset - PickerSearchClearPad),
                },
                PickerSearchTextRise);
            y += PickerSearchHeight;

            // The strips read as refinements OF the search, so they sit below
            // it, inset by the row's own pill inset so pill and rows share one
            // left edge.
            y = DrawStrip(_options.Strip, 0, origin, y, pillInset, rowWidth, scale);
            y = DrawStrip(
                _options.SecondStrip, 1, origin, y, pillInset, rowWidth, scale);

            ImGui.SetCursorScreenPos(origin + new Vector2(0f, y * scale));
            // Half-width bar: bar + its padding = the left content base.
            ScrollRegion(_listId, _panelWidth, _listHeight, _list, pillInset);

            if (_picked != null)
                ImGui.CloseCurrentPopup();
        }

        private static float DrawStrip(
            PickerStrip? strip, int ordinal, Vector2 origin, float y,
            float pillInset, float rowWidth, float scale)
        {
            if (strip is not { } band)
                return y;
            ImGui.SetCursorScreenPos(origin + new Vector2(
                pillInset * scale, (y + PickerListVPad) * scale));
            SegmentedControl(
                $"##picker-strip-{ordinal}",
                band.Labels,
                band.Selected,
                band.OnChange,
                new ControlStyle
                {
                    Width = UiWidth.Fixed(rowWidth),
                    Height = UiHeight.Fixed(ActiveTheme.Controls.NavigationHeight),
                });
            return y + StripHeight();
        }

        // ---- the list ----------------------------------------------------
        private void DrawRows(ScrollRegionScope region)
        {
            _ = region;
            float scale = ImGuiHelpers.GlobalScale;
            // The rows place themselves; ImGui's ambient vertical spacing would
            // inflate the scrolled extent past the last one.
            var spacing = ImGui.GetStyle().ItemSpacing;
            ImGui.PushStyleVar(
                ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
            try
            {
                float pad = PickerListVPad * scale;
                ImGui.Dummy(new Vector2(0f, pad));
                if (_loadError is { } error)
                    EmptyLine(error);
                else if (_visible.Count == 0)
                    EmptyLine("No matches.");
                else
                    ClippedRows(scale);
                // Trailing breathing is INVISIBLE to ImGui's scroll extent — no
                // item covers it — so max-scroll would pin the last pill to the
                // viewport edge without this.
                ImGui.Dummy(new Vector2(0f, pad));
            }
            finally
            {
                ImGui.PopStyleVar();
            }
        }

        /// <summary>The list is clipped at the row pitch, so a catalog of
        /// thousands submits only the band the viewport shows.</summary>
        private void ClippedRows(float scale)
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(_visible.Count, PickerRowPitch * scale);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    Row(_visible[i], scale);
            }
            clipper.End();
        }

        private void Row(T item, float scale)
        {
            var theme = ActiveTheme;
            var draw = ImGui.GetWindowDrawList();
            var bandMin = ImGui.GetCursorScreenPos();
            float pillInset = theme.Scrollbar.GutterWidth * PickerBarShare;
            var pillMin = new Vector2(
                bandMin.X + pillInset * scale,
                bandMin.Y + PickerPillVGap * scale);
            var pillSize = new Vector2(
                MathF.Max(0f, _panelWidth - pillInset * 2f),
                PickerRowHeight) * scale;

            string key = _key(item);
            bool multi = _onToggle is not null;
            bool active = multi
                ? _selectedKeys is { } keys && keys.Contains(key)
                : string.Equals(key, _selectedKey, StringComparison.Ordinal);

            ImGui.SetCursorScreenPos(pillMin);
            var hit = Interactive.Reserve(
                $"{_popupId}-{key}", pillSize, disabled: false);
            ImGui.SetCursorScreenPos(
                new Vector2(bandMin.X, bandMin.Y + PickerRowPitch * scale));

            // Selection carries the stronger overlay and hover the fainter one;
            // hover cascades OVER selection, and the press shares hover's so a
            // held row does not blink.
            var fill = hit.Hovered || hit.Active
                ? theme.Chrome.WeakOverlay
                : active ? theme.Chrome.ActiveOverlay : Vector4.Zero;
            if (fill.W > 0f)
                BoxRenderer.Draw(draw, pillMin, pillMin + pillSize, new BoxStyle
                {
                    BackgroundColor = fill,
                    BorderRadius = theme.Radii.Control,
                });

            float gap = theme.Spacing.Three * scale;
            float x = pillMin.X + PickerRowPadding * scale;
            float centerY = pillMin.Y + pillSize.Y * 0.5f;

            // The multi variant's slot is a real .checkBox; the single
            // variant's is a bare slot holding the same tick. Same box either
            // way, which is what keeps the two variants' labels on one line.
            float slot = PickerCheckSlot * scale;
            var slotMin = new Vector2(x, centerY - slot * 0.5f);
            if (multi)
                PaintCheckBox(
                    draw, slotMin, slotMin + new Vector2(slot), active, theme);
            if (active)
            {
                float tick = PickerCheckGlyph * scale;
                var tickMin = new Vector2(
                    slotMin.X + (slot - tick) * 0.5f, centerY - tick * 0.5f);
                IconIn(
                    tickMin,
                    tickMin + new Vector2(tick),
                    TablerIcon.Check,
                    multi ? theme.Chrome.Checkmark : theme.Text,
                    strokeWidth: PickerCheckStroke);
            }
            x += slot + gap;

            // The caller's resolved image, or the glyph it named as the
            // fallback for the rows the game gives no icon for.
            nint texture = _options.Texture is { } toTexture ? toTexture(item) : 0;
            TablerIcon? glyph = _options.Glyph is { } toGlyph ? toGlyph(item) : null;
            if (texture != 0 || glyph is not null)
            {
                float side = theme.Controls.IconSize * scale;
                var markMin = theme.Optical.Snap(
                    new Vector2(x, centerY - side * 0.5f));
                if (texture != 0)
                    draw.AddImage(
                        new ImTextureID(texture),
                        markMin,
                        markMin + new Vector2(side),
                        Vector2.Zero,
                        Vector2.One,
                        ImGui.ColorConvertFloat4ToU32(
                            ColorEx.ApplyAlpha(Vector4.One)));
                else
                    IconIn(
                        markMin, markMin + new Vector2(side), glyph!.Value,
                        theme.Text);
                x += side + gap;
            }

            float contentRight = pillMin.X + pillSize.X - PickerRowPadding * scale;
            float labelRight = contentRight;
            string? badge = _options.Badge is { } toBadge ? toBadge(item) : null;
            if (!string.IsNullOrEmpty(badge))
            {
                var badgeStyle = new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Family = FontFamily.Mono,
                    Color = theme.FormLabel,
                };
                float width = MeasureText(badge!, badgeStyle).X;
                labelRight = contentRight - width - gap;
                TextInBand(
                    new Vector2(contentRight - width, pillMin.Y),
                    new Vector2(width, pillSize.Y),
                    badge!,
                    badgeStyle,
                    TextAlign.Start,
                    besideIcon: true);
            }

            if (labelRight > x)
                LabelInBand(
                    new Vector2(x, pillMin.Y),
                    new Vector2(labelRight - x, pillSize.Y),
                    _label(item),
                    new TextStyle
                    {
                        Size = theme.Typography.BodySize,
                        Color = theme.Text,
                    },
                    besideIcon: true);

            // Picking is a decision and closes; toggling is not and does not.
            if (!hit.Clicked)
                return;
            if (_onToggle is { } toggle)
                toggle(item, !active);
            else
                _picked = item;
        }

        /// <summary>The list's empty state: one caption on a row band, padded
        /// to where the labels above it would have started.</summary>
        private void EmptyLine(string text)
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = ActiveTheme;
            var bandMin = ImGui.GetCursorScreenPos();
            float pillInset = theme.Scrollbar.GutterWidth * PickerBarShare;
            float left = pillInset + PickerRowPadding + PickerCheckSlot
                + theme.Spacing.Three;
            float right = MathF.Max(
                0f, _panelWidth - pillInset - PickerRowPadding);
            LabelInBand(
                new Vector2(
                    bandMin.X + left * scale, bandMin.Y + PickerPillVGap * scale),
                new Vector2(
                    MathF.Max(0f, right - left) * scale, PickerRowHeight * scale),
                text,
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = FormHintColor,
                });
            ImGui.Dummy(new Vector2(0f, PickerRowPitch * scale));
        }
    }

    /// <summary>
    /// The picker's panel, OPAQUE. The glass shell is translucency, and with no
    /// backdrop blur in game the page bleeds straight through it. Same accepted
    /// resolution as the dropdown popup: the glass tone FLATTENED over the
    /// surface, under a 1px border and the panel shadows, at the popup's own
    /// Surface rounding.
    /// </summary>
    private static void PaintPanel(
        ImDrawListPtr draw, Vector2 min, Vector2 max, Theme theme)
    {
        // The shadows escape the popup's own clip exactly as the dropdown's do.
        draw.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
        try
        {
            BoxRenderer.Draw(draw, min, max, new BoxStyle
            {
                BackgroundColor = ColorEx.FlattenOver(
                    FloatingSurface.FillColor, theme.Surface),
                BorderWidth = 1f,
                BorderRadius = theme.Radii.Surface,
                BorderTopColor = theme.Border,
                BorderRightColor = theme.Border,
                BorderBottomColor = theme.Border,
                BorderLeftColor = theme.Border,
                BoxShadows = [theme.Shadows.Panel, theme.Shadows.PanelRing],
            });
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    /// <summary>OverlayShell's inset bottom hairline, which the caption band
    /// and the search area each carry. Painted inside the box, so it costs the
    /// band no height and nothing above it shifts.</summary>
    private static void PaintRule(
        ImDrawListPtr draw, Vector2 min, Vector2 band, float scale, Theme theme)
    {
        var max = min + band;
        draw.AddRectFilled(
            new Vector2(min.X, max.Y - scale),
            max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Border)));
    }

    /// <summary>OverlayShell's <c>.checkBox</c>: a filled square under a 1px
    /// INSET outline, becoming solid primary with the outline dropped when
    /// checked — which is why the two states are one box and not two.</summary>
    private static void PaintCheckBox(
        ImDrawListPtr draw, Vector2 min, Vector2 max, bool @checked, Theme theme)
    {
        // --color-pressed-overlay is not carried by the generated projection,
        // so it is derived on the same terms as Chrome.DangerHover.
        Vector4? outline = @checked
            ? null
            : theme.Chrome.ActiveOverlay with { W = 0.20f };
        BoxRenderer.Draw(draw, min, max, new BoxStyle
        {
            BackgroundColor =
                @checked ? theme.Chrome.Primary : theme.Chrome.InputWell,
            BorderRadius = theme.Radii.Medium,
            BorderWidth = outline is null ? 0f : 1f,
            BorderTopColor = outline,
            BorderRightColor = outline,
            BorderBottomColor = outline,
            BorderLeftColor = outline,
        });
    }

    /// <summary>Band-centred label, constrained ONLY on overflow: the truncate
    /// clip's snapped edge shaves a fitting run's descender otherwise.
    /// </summary>
    private static void LabelInBand(
        Vector2 min, Vector2 band, string text, in TextStyle style,
        bool besideIcon = false)
    {
        if (!(band.X > 0f))
            return;
        if (MeasureText(text, style).X <= band.X)
            TextInBand(min, band, text, style, TextAlign.Start, besideIcon);
        else
            TextInBand(
                min, band, text, style, TextConstraint.Truncate(band.X),
                TextAlign.Start, besideIcon);
    }
}
